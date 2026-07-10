# 网络层搭建详细设计

**创建时间**：2026-07-09  
**说明**：本文档专门描述 WebSocket 网络层的搭建，包括协议细节、帧格式、流量控制、超时处理等。独立于服务端业务逻辑和客户端 SDK。

---

## 协议概述

FileUploadServer WebSocket 协议采用**双通道**设计：控制通道（JSON 文本帧）和数据通道（二进制帧）。

```
[WebSocket 连接]
  │
  ├── 控制通道 (MessageType.Text)
  │   ├── 请求/响应/心跳
  │   └── JSON 格式
  │
  └── 数据通道 (MessageType.Binary)
      ├── 文件数据
      └── 自定义帧头 + 载荷
```

## 帧格式

### 控制消息帧（JSON）

```
WebSocket MessageType: Text
Payload: UTF-8 JSON

请求帧结构:
{
    "type": "string",        // 消息类型
    "requestId": "uuid",     // 请求唯一标识
    "timestamp": 1234567890, // Unix 时间戳
    // 类型特定字段...
}

响应帧结构:
{
    "type": "string",        // 响应类型（通常为请求类型 + "_ack" 或 "_complete"）
    "requestId": "uuid",     // 对应请求的 ID
    "code": 200,             // 状态码
    "message": "OK",         // 状态消息
    // 类型特定字段...
}
```

### 二进制数据帧

```
WebSocket MessageType: Binary
Payload: 自定义帧头 + 文件数据块

帧头格式 (20 bytes):
┌──────────────────────────────────────────────────────────────────┐
│ 字段          │ 大小   │ 偏移   │ 说明                            │
├────────────────┼────────┼────────┼────────────────────────────────┤
│ requestId      │ 16 B   │ 0      │ GUID，二进制表示              │
│ chunkIndex     │ 4 B    │ 16     │ 大端序 uint32，分块序号       │
│ totalChunks    │ 4 B    │ 20     │ 大端序 uint32，总分块数       │
└────────────────┴────────┴────────┴────────────────────────────────┘

帧结构:
[requestId (16B)] [chunkIndex (4B)] [totalChunks (4B)] [文件数据 (变长)]

总帧头: 24 bytes
建议载荷大小: 64KB (65536 bytes)
```

## 消息类型定义

### 文件操作

| 消息类型 | 方向 | 说明 |
|----------|------|------|
| `upload_request` | 网关→客户端 | 请求上传文件 |
| `upload_ack` | 客户端→网关 | 确认接收上传 |
| `upload_data` | 网关→客户端 | 文件数据块（二进制） |
| `upload_complete` | 客户端→网关 | 上传完成 |
| `upload_error` | 客户端→网关 | 上传失败 |
| `download_request` | 网关→客户端 | 请求下载文件 |
| `download_data` | 客户端→网关 | 文件数据块（二进制） |
| `download_complete` | 客户端→网关 | 下载完成 |
| `download_error` | 客户端→网关 | 下载失败 |
| `delete_request` | 网关→客户端 | 请求删除文件 |
| `delete_complete` | 客户端→网关 | 删除完成 |
| `delete_error` | 客户端→网关 | 删除失败 |

### 心跳消息

| 消息类型 | 方向 | 说明 |
|----------|------|------|
| `ping` | 双向 | 心跳请求 |
| `pong` | 双向 | 心跳响应 |

### 管理消息

| 消息类型 | 方向 | 说明 |
|----------|------|------|
| `register` | 客户端→网关 | 注册客户端信息 |
| `register_ack` | 网关→客户端 | 注册确认 |
| `state_report` | 客户端→网关 | 状态报告（存储用量等） |
| `disconnect` | 双向 | 主动断开通知 |

## 消息序列（Sequence Diagrams）

### 上传序列

```
Client                     Server/Gateway                  User
  │                            │                            │
  │  ◄──── upload_request ─────│◄──── POST /api/upload ─────│
  │                            │                            │
  │  ──── upload_ack ─────────►│                            │
  │                            │                            │
  │  ◄── upload_data (chunk0) ─│  (从用户请求流读取)        │
  │  ◄── upload_data (chunk1) ─│                            │
  │  ◄── upload_data (chunk2) ─│                            │
  │  ◄── upload_data (chunk3) ─│                            │
  │  ...                       │                            │
  │                            │                            │
  │  ──── upload_complete ────►│                            │
  │          {hash, size}      │                            │
  │                            │──── 201 Created ──────────►│
```

### 下载序列

```
Client                     Server/Gateway                  User
  │                            │                            │
  │  ◄── download_request ─────│◄── GET /download/{id} ────│
  │                            │                            │
  │  ── download_data (chunk0)─►│                            │
  │  ── download_data (chunk1)─►│── 流式转发到 Response ───►│
  │  ── download_data (chunk2)─►│── Body ──────────────────►│
  │  ...                       │                            │
  │                            │                            │
  │  ── download_complete ────►│                            │
  │                            │──── 响应完成 ──────────────►│
```

### 心跳序列

```
Client                                          Server
  │                                               │
  │  ──── {"type":"ping"} ───────────────────────►│
  │                                               │── 更新 LastHeartbeat
  │  ◄─── {"type":"pong"} ────────────────────────│
  │                                               │
  │  (30 秒后)                                     │
  │                                               │
  │  ──── {"type":"ping"} ───────────────────────►│
  │  ◄─── {"type":"pong"} ────────────────────────│
  │                                               │
  │  (60 秒无心跳)                                 │
  │                                               │── 标记断开
  │                                               │── 触发 OnClientDisconnected
```

## 错误处理

### 错误消息格式

```json
{
    "type": "error",
    "requestId": "uuid",
    "code": 4001,
    "message": "File not found",
    "details": {}
}
```

### 错误码定义

| 错误码 | 说明 | 处理方式 |
|--------|------|----------|
| `4000` | 未知错误 | 记录日志，返回 500 |
| `4001` | 文件未找到 | 返回 404 |
| `4002` | 路径不合法 | 返回 400 |
| `4003` | 权限不足 | 返回 403 |
| `4004` | 存储空间不足 | 返回 413 |
| `4005` | 文件已存在 | 返回 409 |
| `4006` | 读取超时 | 重试 |
| `4007` | 写入超时 | 重试 |
| `5000` | 服务端内部错误 | 记录日志，返回 502 |
| `5001` | 连接断开 | 自动重连 |
| `5002` | 消息格式错误 | 忽略消息，记录警告 |

## 超时配置

| 超时项 | 默认值 | 说明 |
|--------|--------|------|
| `ConnectTimeout` | 10s | 建立 WebSocket 连接超时 |
| `AckTimeout` | 5s | 等待 ACK 响应超时 |
| `TransferTimeout` | 30s | 文件传输超时（每块） |
| `CompleteTimeout` | 30s | 等待操作完成超时 |
| `HeartbeatInterval` | 30s | 心跳发送间隔 |
| `HeartbeatTimeout` | 5s | 等待 pong 响应超时 |
| `ConnectionTimeout` | 60s | 无心跳最大间隔 |
| `ReconnectMinDelay` | 1s | 重连最小延迟 |
| `ReconnectMaxDelay` | 30s | 重连最大延迟 |

## 流量控制

### 并发控制

```csharp
public class WsFlowController
{
    private readonly SemaphoreSlim _concurrentUploads;
    private readonly SemaphoreSlim _concurrentDownloads;
    private readonly long _maxBytesPerSecond;

    public WsFlowController(int maxConcurrentUploads = 10,
                             int maxConcurrentDownloads = 20,
                             long maxBytesPerSecond = 100 * 1024 * 1024) // 100 MB/s
    {
        _concurrentUploads = new SemaphoreSlim(maxConcurrentUploads);
        _concurrentDownloads = new SemaphoreSlim(maxConcurrentDownloads);
        _maxBytesPerSecond = maxBytesPerSecond;
    }

    public async Task<IDisposable> AcquireUploadSlot()
        => await _concurrentUploads.UseWaitAsync();

    public async Task<IDisposable> AcquireDownloadSlot()
        => await _concurrentDownloads.UseWaitAsync();
}
```

### 背压机制

当服务端转发速度慢于用户接收速度时，使用 `Pipe` 的自然背压：

```csharp
// 下载时，服务端从 WS 客户端读取数据，写入 Pipe
// 再从 Pipe 读取写入用户 HTTP Response
// 如果用户消费慢，Pipe 填满后自动减慢 WS 读取

var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 65536 * 4)); // 256KB buffer
var writer = pipe.Writer;
var reader = pipe.Reader;

// Task 1: 从 WS 客户端读取 → 写入 Pipe
_ = Task.Run(async () =>
{
    while (true)
    {
        var data = await wsClient.ReceiveAsync();
        var flushResult = await writer.WriteAsync(data);
        if (flushResult.IsCompleted) break;
    }
    writer.Complete();
});

// Task 2: 从 Pipe 读取 → 写入 HTTP Response
_ = Task.Run(async () =>
{
    while (true)
    {
        var readResult = await reader.ReadAsync();
        foreach (var segment in readResult.Buffer)
        {
            await httpResponse.Body.WriteAsync(segment);
        }
        reader.AdvanceTo(readResult.Buffer.End);
        if (readResult.IsCompleted) break;
    }
    reader.Complete();
});
```

## 安全设计

### 连接认证

```
1. 客户端计算 token:
   token = HMAC-SHA256(clientSecret, clientId + ":" + timestamp)
   或
   token = SHA256(clientId + ":" + clientSecret + ":" + timestamp)

2. 服务端验证:
   a. 查找 ws_clients 表获取 clientSecretHash
   b. 验证 timestamp 在 ±5 分钟内
   c. 计算预期 token 并比较
```

### 路径安全检查

```csharp
public static bool IsValidPath(string path)
{
    // 1. 必须 / 开头
    if (!path.StartsWith('/')) return false;

    // 2. 规范化路径
    var normalized = Path.GetFullPath(path);  // Linux: 移除 ./../
    // 注意：Path.GetFullPath 在 Linux 上行为不同

    // 3. 不能包含 ..
    if (normalized.Contains("..")) return false;

    // 4. 不能包含空字符
    if (path.Contains('\0')) return false;

    // 5. 最大长度
    if (path.Length > 1024) return false;

    // 6. 只允许可见 ASCII 和常见 Unicode
    if (!path.All(c => c >= 32 && c <= 0x10FFFF)) return false;

    return true;
}
```

## 性能目标

| 指标 | 目标值 |
|------|--------|
| 单连接吞吐量 | ≥ 500 Mbps |
| 同时在线客户端数 | ≥ 1000 |
| 并发转发数 | ≥ 200 |
| 消息延迟（P95） | ≤ 50ms |
| 重连恢复时间 | ≤ 30s |
| 心跳误判率 | ≤ 0.1% |

## 网络层测试

### 协议一致性测试

```csharp
[Fact]
public async Task UploadRequest_Format_IsValidJson()
{
    var message = new UploadRequestMessage
    {
        Type = "upload_request",
        RequestId = Guid.NewGuid(),
        Path = "/test/file.txt",
        FileSize = 1024
    };
    var json = JsonSerializer.Serialize(message);
    Assert.Contains("\"type\":\"upload_request\"", json);
    Assert.Contains("\"path\":\"/test/file.txt\"", json);
}

[Fact]
public async Task BinaryFrame_Header_ParsesCorrectly()
{
    var requestId = Guid.NewGuid();
    var frame = BuildBinaryFrame(requestId, chunkIndex: 0, totalChunks: 5, data);
    var (parsedId, chunkIdx, totalChunks, payload) = ParseBinaryFrame(frame);
    Assert.Equal(requestId, parsedId);
    Assert.Equal(0u, chunkIdx);
    Assert.Equal(5u, totalChunks);
}
```

### 网络异常测试

```csharp
[Theory]
[InlineData("network_unreachable")]
[InlineData("connection_refused")]
[InlineData("tls_handshake_failed")]
[InlineData("timeout")]
public async Task Connect_WithNetworkErrors_Retries(string errorType)
{
    // Arrange: 模拟网络错误
    // Act: 客户端连接
    // Assert: 指数退避重连
}

[Fact]
public async Task LargeFile_Transfer_ResumesAfterReconnect()
{
    // Arrange: 传输 100MB 文件
    // Act: 传输中断线后重连
    // Assert: 文件完整
}
```

### 模糊测试

```
对 WebSocket 消息进行模糊测试：
- 截断的消息
- 超大消息头
- 无效 UTF-8
- 重复 requestId
- 乱序的 chunkIndex
- 负数的 chunkIndex
- 超大 chunkIndex
```

## 部署网络拓扑

### 单机部署

```
[用户] ──HTTP──▶ [FileUploadServer:5005]
                          │
                    [WS 客户端: 本地]
                    [LocalFileStorageClient]
```

### 生产部署

```
[用户] ──HTTPS──▶ [Nginx/CDN]
                      │
                反向代理
                      │
            [FileUploadServer:443]
               │              │
         WS 连接            WS 连接
               │              │
         [Client A]      [Client B]
         /mnt/storage1   /mnt/storage2
```

### 高可用部署

```
[用户] ──HTTPS──▶ [负载均衡器 (HAProxy/Nginx)]
                      │
            ┌─────────┼─────────┐
            ▼         ▼         ▼
      [FS Node 1] [FS Node 2] [FS Node 3]
       (WS 网关)   (WS 网关)   (WS 网关)
            │         │         │
            └─────────┼─────────┘
                      │
            ┌─────────┼─────────┐
            ▼         ▼         ▼
      [Client A] [Client B] [Client C]
       (主存储)   (主存储)   (主存储)
```

**注意**：在多网关节点场景下，客户端连接到哪个网关节点由负载均衡器决定。需要确保：
1. 客户端 ID 全局唯一
2. 所有网关节点共享相同的 `ws_clients` 数据库
3. 文件路由信息（FileLocation）在所有节点一致
