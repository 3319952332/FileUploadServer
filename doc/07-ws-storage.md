# WS 分布式存储细案

> 用途：详细描述网关与 WS 存储节点间的 WebSocket 协议——连接管理、二进制帧、消息类型、路由策略、消息处理器及存储策略，作为 [01-architecture.md](01-architecture.md) 第 6 节"WS 协议"的深化补充。
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md) / [00-overview.md](00-overview.md)

## 目录

1. [架构总览](#架构总览)
2. [二进制帧协议](#二进制帧协议)
3. [消息类型定义](#消息类型定义)
4. [WsClient 端：WsFileStorageClient](#wsclient-端wsfilestorageclient)
5. [WsClient 端：LocalFileStorageClient 降级](#wsclient-端localfilestorageclient-降级)
6. [网关侧：WebSocketHandlerMiddleware](#网关侧websockethandlermiddleware)
7. [网关侧：WsConnectionManager](#网关侧wsconnectionmanager)
8. [网关侧：ClientRouter 路由策略](#网关侧clientrouter-路由策略)
9. [网关侧：WsStorageStrategy](#网关侧wsstoragestrategy)
10. [消息处理器](#消息处理器)
11. [上传/下载时序](#上传下载时序)
12. [关键类/文件](#关键类文件)
13. [关联文档](#关联文档)

---

## 架构总览

网关（FileUploadServer.Web）不直接存储文件，职责是 **认证、路由分发和元数据管理**。实际文件磁盘 I/O 由 WS 存储节点（FileUploadServer.WsClient）承担。

```
┌──────────────┐       WebSocket        ┌──────────────────────┐
│  网关 (Web)   │◄──────────────────────►│  WS 存储节点 (WsClient)│
│              │  /ws/connect?token=...  │                      │
│  认证/路由    │  JSON文本帧 + 二进制帧   │  磁盘I/O              │
│  元数据(PG)   │                         │  SHA256校验           │
└──────────────┘                         └──────────────────────┘
```

无在线 WS 节点时，网关降级为本地存储（LocalStorageStrategy → 本地磁盘 `wwwroot/uploads`）。

---

## 二进制帧协议

文件数据通过 WebSocket Binary 帧传输，使用自定义 24 字节帧头 + 可变载荷。

### 帧结构

```
┌──────────────────────────────────────────────────────────────┐
│ 帧头（24 字节）                                                │
├───────────────────┬──────────────────────────────────────────┤
│ Offset 0   (16B)  │ requestId (Guid, 二进制)                  │
│ Offset 16  (4B)   │ chunkIndex (uint32, 大端序)               │
│ Offset 20  (4B)   │ totalChunks (uint32, 大端序)              │
├───────────────────┼──────────────────────────────────────────┤
│ 帧头之后          │ payload (文件数据, 变长, 0 ~ MaxPayload)   │
└───────────────────┴──────────────────────────────────────────┘
```

来源：`FileUploadServer.WsClient/Protocol/WsBinaryFrame.cs`。

### 常量

| 常量 | 值 | 说明 |
|---|---|---|
| `HeaderSize` | 24 字节 | 帧头固定大小 |
| `ChunkSize` | 65536 (64KB) | 建议分块大小 |
| `MaxPayloadSize` | 1048576 (1MB) | 实际支持的最大帧载荷 |

### 帧操作

- `BuildFrame(requestId, chunkIndex, totalChunks, data, dataLength)` — 构造完整二进制帧
- `ParseFrame(frame)` — 解析为 `(requestId, chunkIndex, totalChunks, payload)` 元组

`chunkIndex` 从 0 开始；`totalChunks = -1` 表示总分块数未知（流式传输场景）。

---

## 消息类型定义

协议采用 **双通道** 设计：

- **控制通道**：JSON 文本帧（`WebSocketMessageType.Text`），camelCase 序列化
- **数据通道**：自定义二进制帧（`WebSocketMessageType.Binary`），仅文件数据

来源：`FileUploadServer.Core/Models/WsMessageTypes.cs`。

### 11 种消息类型表

| 消息类型 | 方向 | 通道 | 载荷字段 | 说明 |
|---|---|---|---|---|
| `upload_request` | 网关 → 节点 | 文本 | `path`, `fileName`, `fileSize` | 请求上传，携带目标路径和大小 |
| `upload_ack` | 节点 → 网关 | 文本 | `code`(200), `message`("OK") | 确认接收上传，准备接收数据 |
| `upload_complete` | 节点 → 网关 | 文本 | `fileHash`(SHA256), `fileSize`, `code`, `message` | 上传完成，含文件哈希 |
| `download_request` | 网关 → 节点 | 文本 | `path` | 请求下载 |
| `download_data` | 节点 → 网关 | 文本+二进制 | 控制帧: `chunkIndex`, `totalChunks`；二进制帧紧随 | 每块数据对应一个控制帧+二进制帧对 |
| `download_complete` | 节点 → 网关 | 文本 | `code`(200), `message`("OK") | 下载完成 |
| `delete_request` | 网关 → 节点 | 文本 | `path` | 请求删除 |
| `delete_complete` | 节点 → 网关 | 文本 | `code`(204), `message`("Deleted") | 删除完成 |
| `list_request` | 网关 → 节点 | 文本 | `pathPrefix`, `includePublic`, `skip`, `take`(max 1000) | 列出文件（ListRequestHandler） |
| `ping` / `pong` | 双向 | 文本 | `timestamp`, `echoTimestamp`(pong) | 心跳保活 |
| `error` | 双向 | 文本 | `code`, `message` | 错误通知 |

### 消息基类

所有 JSON 消息继承自 `WsMessageBase`，包含三个公共字段：

- `type` — 消息类型字符串
- `requestId` — 请求唯一标识（GUID 字符串，关联请求-响应）
- `timestamp` — Unix 时间戳（秒）

---

## WsClient 端：WsFileStorageClient

来源：`FileUploadServer.WsClient/WsFileStorageClient.cs`。

`WsFileStorageClient` 实现 `IFileStorageClient` 接口，是 WS 存储节点的核心客户端。

### 认证流程

连接到网关前计算认证 token：

```
token = SHA256(clientId + ":" + SHA256(clientSecret) + ":" + timestamp)
```

即：先对 `clientSecret` 做 SHA256 得到哈希（网关存储的也是该哈希），再与 `clientId`、`timestamp` 拼接后做二次 SHA256。

连接 URL 格式：

```
/ws/connect?clientId={id}&token={token}&timestamp={unix}&prefixes={prefix1,prefix2}
```

`prefixes` 参数声明本节点负责的路径前缀（逗号分隔，URL 编码），网关据此路由。

### 生命周期与心跳

```
                     ┌─────────────┐
        ┌───────────►│   已连接     │◄────────────┐
        │            │ IsConnected │             │
        │            └──────┬──────┘             │
        │                   │                    │
        │            ping(30s)│断线/心跳超时       │
        │                   ▼                    │
        │            ┌──────────────┐            │
        │            │    重连中     │            │
        │            │ attempt=N    ├────────────┘
        │            └──────┬───────┘   指数退避重连
        │                   │ 连接成功
        │                   ▼
        └───────────────────┘
```

- **心跳间隔**：30 秒发送一次 `ping`
- **心跳发送**：Timer 定时触发 `SendHeartbeat()`
- **心跳失败**：不立即重连，由 ReceiveLoop 检测断线后自动触发重连

### 指数退避重连

断线后按固定延迟序列重试：

```
[1000, 2000, 4000, 8000, 15000, 30000] ms
```

第 N 次重连等待 `ReconnectDelays[min(N-1, 5)]` 毫秒。超过数组长度后固定 30s。如果重连失败（连接异常），递归调用 `ReconnectAsync()` 继续尝试，直到手动 `DisconnectAsync()` 或 `DisposeAsync()`。

### 请求-响应模式

客户端维护两个并发字典管理异步请求：

- `_pendingRequests`：`ConcurrentDictionary<string, TaskCompletionSource<ResponseMessage>>` — 等待网关响应的请求
- `_downloads`：`ConcurrentDictionary<string, DownloadContext>` — 活跃的下载/上传流上下文

**上传流程**（`WriteFileAsync`）：

1. 发送 `upload_request`（JSON）→ 注册 `TaskCompletionSource`
2. 等待 `upload_ack`（5s 超时）
3. 循环：`data.ReadAsync(buffer)` → `BuildFrame()` → `SendBinaryAsync()`，每块 64KB
4. 等待 `upload_complete`（30s 超时）

**下载流程**（`ReadFileAsync`）：

1. 创建 `Pipe`（256KB 暂停阈值），注册 `DownloadContext`
2. 发送 `download_request`（JSON）
3. 二进制帧到达时由 `HandleBinaryMessage` 写入 `PipeWriter`
4. 等待 `download_complete`（30s 超时）
5. 返回 `pipe.Reader.AsStream()` 流

**删除流程**（`DeleteFileAsync`）：

1. 发送 `delete_request`（JSON）
2. 等待 `delete_complete`（10s 超时）

### ReceiveLoop 消息分发

`ReceiveLoopAsync()` 循环接收 WebSocket 帧，按类型分发：

- **Text 帧** → 累积分片组装完整 JSON → `HandleTextMessage()` → 按 `type` 字段路由到对应 handler
- **Binary 帧** → `HandleBinaryMessage()` → 解析帧头 → 写入 `_downloads` 对应上下文
- **Close 帧** → `HandleClose()` → 发送响应后退出循环

### 路径安全

`GetSafePath()` 校验：拒绝包含 `..` 的路径，拒绝空字符，路径长度限制 1024 字符。

---

## WsClient 端：LocalFileStorageClient 降级

来源：`FileUploadServer.WsClient/Storage/LocalFileStorageClient.cs`。

`LocalFileStorageClient` 同样实现 `IFileStorageClient`，但所有操作直接映射本地磁盘 I/O，无需网络连接：

- `ConnectAsync` / `DisconnectAsync` → 空操作（本地无需连接）
- `IsConnected` → 始终 `true`
- `OnDisconnected` → 永不触发
- 路径安全同 `WsFileStorageClient`：拒绝 `..`、空字符、长度 > 1024，使用 `Path.GetFullPath` + 前缀校验防御遍历

该客户端用于：
- 单机部署（无需 WS 存储节点）
- 测试环境
- 网关本地磁盘降级（WsClient 项目自身也可独立使用）

---

## 网关侧：WebSocketHandlerMiddleware

来源：`FileUploadServer.Web/Middleware/WebSocketHandlerMiddleware.cs`。

### 职责

拦截 `GET /ws/connect`，完成客户端认证、WebSocket 升级、连接注册、消息接收循环。

### 认证与升级

1. 读取查询参数 `clientId`、`token`、`timestamp`
2. 调用 `WsClientAuthService.ValidateConnectionAsync(clientId, token, timestamp)` 验证身份
3. 确认是否为 WebSocket 请求（`context.WebSockets.IsWebSocketRequest`）
4. 调用 `context.WebSockets.AcceptWebSocketAsync()` 升级
5. 从 `prefixes` 参数提取路径前缀列表
6. 注册到 `WsConnectionManager`：`RegisterConnectionAsync(clientId, webSocket, pathPrefixes)`
7. 更新首次心跳：`UpdateHeartbeat(clientId)`

### ReceiveLoop 主循环

```csharp
while (webSocket.State == WebSocketState.Open)
{
    receiveResult = await webSocket.ReceiveAsync(...);
    
    if (receiveResult.MessageType == WebSocketMessageType.Text)
        → 累积分片JSON → 后台 Task ProcessTextMessageAsync()
    else if (receiveResult.MessageType == WebSocketMessageType.Binary)
        → ProcessBinaryFrameAsync()
    else if (receiveResult.MessageType == WebSocketMessageType.Close)
        → Close + break
}
```

**Text 帧分发逻辑**（`ProcessTextMessageAsync`）：

1. 解析 JSON → 提取 `type` 和 `requestId`
2. `ping`/`pong` → 更新心跳时间戳
3. 如果 `requestId` 在 `PendingResponses` 中存在 → `TrySetResult(doc)`，完成等待中的 `WsStorageStrategy` 请求
4. 否则 → 按 `type` 从 `handlerDict` 查找 `IMessageHandler` → `handler.HandleAsync()`
5. 无匹配 handler → 回复 `error` (code=5002)

**Binary 帧分发逻辑**（`ProcessBinaryFrameAsync`）：

1. 解析帧头 `(requestId, chunkIndex, totalChunks)`
2. 优先检查 `PendingDownloadStreams`（WS 下载场景）
3. 其次检查 `PendingUploads`（ws 上传场景）
4. 无匹配 → 警告日志

### PendingUpload 上下文

`PendingUpload` 管理分块上传状态：
- 接收二进制块 → `WriteChunkAsync(chunkIndex, data, totalChunks)`
- 完成判定：`receivedCount >= expectedTotalChunks || receivedCount >= totalChunks`
- 支持 `WaitForCompletionAsync(timeout)` 带超时等待
- 取消时清理临时文件和流

### 静态辅助方法

- `SendJsonAsync(webSocket, object)` — 发送 JSON 作为 Text 帧
- `SendBinaryAsync(webSocket, requestId, chunkIndex, totalChunks, data)` — 发送二进制帧
- `BuildBinaryFrame(requestId, chunkIndex, totalChunks, data)` — 构建二进制帧
- `RegisterPendingUpload(clientId, requestId, totalChunks, writeStream)` — 注册待处理上传
- `RemovePendingUpload(requestId)` — 移除待处理上传

### 三个静态字典（跨中间件与策略组件通信）

| 字典 | 键 | 值 | 用途 |
|---|---|---|---|
| `PendingUploads` | requestId | `PendingUpload` | 中间件接收二进制帧 → 写入待处理上传 |
| `PendingResponses` | requestId | `TaskCompletionSource<JsonDocument>` | WsStorageStrategy 等待 WS 客户端的文本响应 |
| `PendingDownloadStreams` | requestId | `MemoryStream` | WsStorageStrategy.ReadAsync 接收二进制下载数据 |

---

## 网关侧：WsConnectionManager

来源：`FileUploadServer.Web/Services/WsConnectionManager.cs`。

### 职责

管理所有活跃 WS 客户端连接的生命周期，维护路径前缀索引以支持快速路由。

### 连接池

```csharp
ConcurrentDictionary<string, WsClientConnection> _connections          // clientId → 连接
ConcurrentDictionary<string, HashSet<string>> _pathPrefixIndex        // 路径前缀 → clientId 集合
```

### 注册/注销

- `RegisterConnectionAsync(clientId, webSocket, pathPrefixes)`：
  - 如果同 clientId 已有连接，取消旧连接（DisconnectCts.Cancel()）
  - 将新连接加入 `_connections`，更新 `_pathPrefixIndex`
- `UnregisterConnectionAsync(clientId)`：
  - 从 `_connections` 移除
  - 从 `_pathPrefixIndex` 清除所有相关条目
  - 尝试 Close WebSocket

### 路由查询

- `GetConnectionsForPath(filePath)` — 返回所有路径前缀匹配且 WebSocket 状态为 Open 的连接
- `TryPickClientForPath(filePath, out client)` — 委托给 `ClientRouter.SelectClient()`，返回最适合的连接

### 心跳检测

- `StartHeartbeatCheck()` — 启动 30s 间隔 Timer
- `UpdateHeartbeat(clientId)` — 更新 `LastHeartbeat = UtcNow`
- 超时判定：`now - LastHeartbeat > 60s` → 自动注销连接

### WsClientConnection 实体

```csharp
class WsClientConnection {
    string ClientId;           // 客户端唯一标识
    WebSocket WebSocket;       // 底层连接
    DateTime ConnectedAt;      // 连接建立时间
    DateTime LastHeartbeat;    // 最后心跳时间
    long TotalStorageBytes;    // 当前存储字节数（用于 LeastStorage 路由）
    List<string> SupportedPaths; // 支持的路径前缀
    CancellationTokenSource DisconnectCts; // 断开令牌
}
```

---

## 网关侧：ClientRouter 路由策略

来源：`FileUploadServer.Web/Services/ClientRouter.cs`。

### 四种路由策略

| 策略 | 枚举值 | 选择逻辑 | 适用场景 |
|---|---|---|---|
| 路径前缀匹配 | `PathPrefix` | 按路径前缀匹配最长者优先，同前缀按健康度降序 | 最常用，多前缀部署 |
| 轮询 | `RoundRobin` | 同一前缀下轮流选择，原子计数器 | 同质节点负载均衡 |
| 最少存储 | `LeastStorage` | `TotalStorageBytes` 最小者优先 | 平衡各节点磁盘使用 |
| 加权随机 | `WeightedRandom` | 按 `TotalStorageBytes` 加权随机（容量越大权重越高） | 异构节点部署 |

### 选择流程

`SelectClient(filePath, allConnections)`：

1. **过滤**：WebSocket 状态为 Open 且路径前缀匹配（支持 `*` / `**` 通配符）
2. **冷却检查**：排除故障转移冷却中的客户端
3. **降级**：全部冷却中时，从冷却中选健康度最高的
4. **策略选择**：按配置的策略从候选列表中选出最优

### 故障转移与冷却

- `MarkUnavailable(clientId)` — 标记为不可用，进入 30 秒冷却期
- `IsInCooldown(clientId)` — 检查是否在冷却中（自动清理过期记录）
- `ClearCooldowns()` — 清除所有冷却标记

### 健康度评分

`CalculateHealthScore(connection)`：
- 基础分 100
- 心跳延迟扣分：每秒 -2 分
- 连接状态不佳（非 Open）扣 50 分
- 最低 0 分

---

## 网关侧：WsStorageStrategy

来源：`FileUploadServer.Web/Services/WsStorageStrategy.cs`。

`WsStorageStrategy` 实现 `IStorageStrategy` 接口，通过 WebSocket 将文件操作转发到远程节点。

### ReadAsync — 远程下载

```
1. ClientRouter 选节点
2. 注册 PendingDownloadStreams[requestId] = new MemoryStream()
3. SendJson(download_request)
4. 等待 download_complete (PendingResponses, 30s 超时)
5. stream.Position = 0 → 返回
```

二进制帧到达时由中间件 `ProcessBinaryFrameAsync` 写入 `PendingDownloadStreams`。

### WriteAsync — 远程上传

```
1. ClientRouter 选节点
2. SendJson(upload_request, path, fileName, fileSize)
3. 等待 upload_ack (PendingResponses, 5s 超时)
4. 循环分块: SendBinaryAsync(chunk), chunkIndex++
5. SendJson(upload_complete) → 等待 upload_complete (PendingResponses, 30s 超时)
```

### DeleteAsync — 远程删除

```
1. ClientRouter 选节点（找不到则返回，不抛异常）
2. SendJson(delete_request, path)
3. 等待 delete_complete (PendingResponses, 30s 超时)
```

### 与共享服务的集成（FileDownloadService / FileDeleteService）

`WsStorageStrategy` 不直接面向业务入口，业务层通过两个共享服务调用：

- **下载解密** `FileDownloadService.OpenDecryptedStreamAsync`（`Web/Services/FileDownloadService.cs`）：内部判断 `StorageMode == "WebSocket"` 时调用 `WsStorageStrategy.ReadAsync`，再按 `EncryptionVersion` 包装 `AesGcmDecryptStream`。网页 / API / 公共访问三入口统一走它。
- **删除清理** `FileDeleteService.DeletePhysicalAsync`（`Web/Services/FileDeleteService.cs`）：内部调用 `WsStorageStrategy.DeleteAsync` 删除远程文件，并清理 `FileLocation` 记录 + 本地物理文件（含加密子目录）。网页 / API 删除统一走它，避免 WS 节点密文残留。

---

## 消息处理器

网关侧 5 个 `IMessageHandler` 实现，处理 WS 节点发来的业务请求。来源：`FileUploadServer.Web/MessageHandlers/` 目录。

### UploadRequestHandler

流程：接收 `upload_request` → 路径校验 → 计算分块数 → 创建临时文件 + `RegisterPendingUpload` → 发送 `upload_ack` → `WaitForCompletionAsync(300s)` → 计算 `SHA256` → `StorageStrategy.WriteAsync` 存储 → 写入 `FileLocation` 记录（写入数据库，无关联 ApiKey）→ 清理临时文件 → 发送 `upload_complete`

安全校验：
- 路径必须以 `/` 开头
- 拒绝 `..`、空字符 `\0`
- 路径长度 ≤ 1024

### DownloadRequestHandler

流程：接收 `download_request` → 路径校验 → `StorageStrategy.ReadAsync` → 使用 Pipe 流式传输 → 按 64KB 分块 → `SendBinaryAsync` 逐块发送 → 发送 `download_complete`（含 `totalChunks`, `fileSize`）

### DeleteRequestHandler

流程：接收 `delete_request` → 路径校验 → 查找 `FileLocation` 记录（`clientId + path` 匹配） → `StorageStrategy.DeleteAsync` 删除存储文件 → 删除数据库 `FileLocation` 记录 → 发送 `delete_complete`

### ListRequestHandler

流程：接收 `list_request(pathPrefix, includePublic, skip, take)` → 查询 `FileLocation` 表（按 `clientId` + 路径前缀过滤） → 按 `CreatedAt` 倒序，分页 → 发送 `list_response(total, skip, take, files[])`

参数限制：`take` 最大 1000。

### PingPongHandler

收到 `ping` → 回复 `pong`（含 `requestId`、`echoTimestamp`、`timestamp`）。心跳更新时间戳已在中间件层处理。

---

## 上传/下载时序

### 上传时序

```
网关 Web                           WS 节点
   │                                  │
   │  upload_request (JSON)           │
   │──────────────────────────────────►│
   │                                  │ 创建目录 + 打开文件写入流
   │  upload_ack (JSON)               │
   │◄──────────────────────────────────│
   │                                  │
   │  Binary Frame [chunk=0/4]        │
   │──────────────────────────────────►│ 写入文件流
   │  Binary Frame [chunk=1/4]        │
   │──────────────────────────────────►│
   │  Binary Frame [chunk=2/4]        │
   │──────────────────────────────────►│
   │  Binary Frame [chunk=3/4]        │
   │──────────────────────────────────►│
   │                                  │
   │  upload_complete (JSON)          │
   │──────────────────────────────────►│ SHA256 → 最终存储
   │  upload_complete (JSON)          │
   │◄──────────────────────────────────│ fileHash, fileSize
```

> **网关本地临时副本清理**：业务上传流程（`FileApiController.Upload` / `Index.cshtml.cs` OnPostAsync）会先在网关本地 `wwwroot/uploads` 加密写一份临时副本，再经 `WsStorageStrategy.WriteAsync` 转发 WS 节点。**WS 转发成功后立即删除本地临时副本**（本地仅作中转，正式存储为 WS 节点）；WS 转发失败降级本地时保留本地文件。这避免网关本地累积无记录对应的孤儿密文（存量孤儿已手动清理，根治记录见 [12-bug-tracker.md](12-bug-tracker.md)）。

### 下载时序

```
网关 Web                           WS 节点
   │                                  │
   │  download_request (JSON)         │
   │──────────────────────────────────►│
   │                                  │ 打开文件读流
   │  download_data 控制帧 +          │
   │  Binary Frame [chunk=0/4]        │
   │◄──────────────────────────────────│
   │  download_data 控制帧 +          │
   │  Binary Frame [chunk=1/4]        │
   │◄──────────────────────────────────│
   │  ...                             │
   │  download_complete (JSON)        │
   │◄──────────────────────────────────│
```

---

## 关键类/文件

| 文件 | 关键类 | 职责 |
|---|---|---|
| `FileUploadServer.WsClient/Protocol/WsBinaryFrame.cs` | `WsBinaryFrame` | 二进制帧构建/解析 |
| `FileUploadServer.WsClient/Protocol/WsMessageSerializer.cs` | `WsMessageSerializer` | JSON 序列化/按 type 反序列化 |
| `FileUploadServer.WsClient/WsFileStorageClient.cs` | `WsFileStorageClient` | WS 节点核心客户端 |
| `FileUploadServer.WsClient/Storage/LocalFileStorageClient.cs` | `LocalFileStorageClient` | 本地降级客户端 |
| `FileUploadServer.Core/Models/WsMessageTypes.cs` | 11 个消息类 | 协议数据结构 |
| `FileUploadServer.Web/Middleware/WebSocketHandlerMiddleware.cs` | `WebSocketHandlerMiddleware`, `PendingUpload` | WS 升级/分发/上传上下文 |
| `FileUploadServer.Web/Services/WsConnectionManager.cs` | `WsConnectionManager`, `WsClientConnection` | 连接池/心跳检测 |
| `FileUploadServer.Web/Services/ClientRouter.cs` | `ClientRouter` | 4 策略路由/故障转移/健康评分 |
| `FileUploadServer.Web/Services/WsStorageStrategy.cs` | `WsStorageStrategy` | WS 远程读写删 |
| `FileUploadServer.Web/MessageHandlers/UploadRequestHandler.cs` | `UploadRequestHandler` | 服务端上传处理 |
| `FileUploadServer.Web/MessageHandlers/DownloadRequestHandler.cs` | `DownloadRequestHandler` | 服务端下载处理 |
| `FileUploadServer.Web/MessageHandlers/DeleteRequestHandler.cs` | `DeleteRequestHandler` | 服务端删除处理 |
| `FileUploadServer.Web/MessageHandlers/ListRequestHandler.cs` | `ListRequestHandler` | 服务端列表处理 |
| `FileUploadServer.Web/MessageHandlers/PingPongHandler.cs` | `PingPongHandler` | 心跳回复 |

---

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览、WS 协议概览（第 6 节）
- [02-api-reference.md](02-api-reference.md) — HTTP API 完整参考
- [00-overview.md](00-overview.md) — 项目总览
- 旧参考：`doc/IMPLEMENTATION_PLAN_NETWORK.md` — WS 协议与帧格式原始设计（设计与实现一致）
- 旧参考：`doc/IMPLEMENTATION_PLAN_CLIENTS.md` — WS 节点规划文档
