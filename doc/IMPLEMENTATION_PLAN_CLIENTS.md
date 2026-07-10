# WS 客户端 SDK 设计文档

**创建时间**：2026-07-09  
**说明**：本文档独立于服务端实现计划，专门描述 WS 客户端 SDK 的设计与实现，可独立开发和测试。

---

## 概述

WS 客户端是 FileUploadServer WebSocket 架构中的"文件存储执行者"。服务端（网关）不存储文件，仅负责认证、路由和转发。每个 WS 客户端连接到服务端后，负责实际的文件磁盘 I/O。

## 客户端类型

```
┌─────────────────────────────────────────────────────┐
│  IFileStorageClient (接口)                            │
├─────────────────────────────────────────────────────┤
│  + ConnectAsync(serverUrl, clientId, clientSecret)   │
│  + DisconnectAsync()                                 │
│  + ReadFileAsync(path) → Stream                      │
│  + WriteFileAsync(path, data)                        │
│  + DeleteFileAsync(path)                             │
│  + FileExistsAsync(path) → bool                      │
│  + GetFileSizeAsync(path) → long                     │
│  + GetFileHashAsync(path) → string                   │
└──────────────────┬──────────────────────────────────┘
                   │ 实现
         ┌─────────┼──────────┐
         ▼         ▼          ▼
  LocalFile     WsFile     RemoteFile
  StorageClient Storage    StorageClient
  (本地磁盘)     Client      (HTTP API)
               (WebSocket)   (远程调用)
```

### 1. LocalFileStorageClient

**用途**：降级模式 / 单机部署 / 测试

```csharp
public class LocalFileStorageClient : IFileStorageClient
{
    private readonly string _basePath;

    public LocalFileStorageClient(string basePath)
    {
        _basePath = basePath;
    }

    public Task ConnectAsync(...) => Task.CompletedTask;  // 无需连接
    public Task DisconnectAsync() => Task.CompletedTask;

    public async Task<Stream> ReadFileAsync(string path)
    {
        var fullPath = GetFullPath(path);
        return File.OpenRead(fullPath);
    }

    public async Task WriteFileAsync(string path, Stream data)
    {
        var fullPath = GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var fileStream = File.Create(fullPath);
        await data.CopyToAsync(fileStream);
    }

    public Task DeleteFileAsync(string path)
    {
        var fullPath = GetFullPath(path);
        File.Delete(fullPath);
        return Task.CompletedTask;
    }

    // ...其他方法
}
```

### 2. WsFileStorageClient（核心）

**用途**：远程 WS 客户端，通过 WebSocket 连接到服务器

#### 架构

```
WsFileStorageClient
  │
  ├── ClientWebSocket (System.Net.WebSockets)
  ├── HeartbeatTimer (30s)
  ├── ReconnectPolicy (指数退避)
  ├── PendingRequests: ConcurrentDictionary<string, TaskCompletionSource<Response>>
  └── MessageSerializer (JSON + Binary)
```

#### 连接流程

```
ConnectAsync(serverUrl, clientId, clientSecret):
  │
  ├─ 1. 构造 URL: ws://server/ws/connect?clientId={id}&token={token}
  │     token = SHA256(clientId + ":" + clientSecret + ":" + timestamp)
  │
  ├─ 2. ClientWebSocket.ConnectAsync(url)
  │
  ├─ 3. 启动接收循环 (ReceiveLoopAsync)
  │     ├─ 后台 Task，持续运行
  │     ├─ 接收文本帧 → 解析 JSON → 分派到对应处理器
  │     └─ 接收二进制帧 → 匹配 requestId → 写入对应流
  │
  ├─ 4. 启动心跳 (StartHeartbeatAsync)
  │     └─ 每 30s 发送 {"type":"ping"}
  │
  └─ 5. 设置 IsConnected = true
```

#### 接收循环 (ReceiveLoopAsync)

```
ReceiveLoopAsync():
  │
  ├─ while (WebSocket.State == WebSocketState.Open):
  │   │
  │   ├─ ReceiveAsync() → MessageType
  │   │
  │   ├─ if MessageType == Text:
  │   │   ├─ 读取完整 JSON 消息
  │   │   ├─ 解析 type + requestId
  │   │   └─ 匹配 PendingRequests[requestId]
  │   │       ├─ 找到 → Complete TaskCompletionSource
  │   │       └─ 未找到 → 作为服务端主动消息处理
  │   │
  │   ├─ if MessageType == Binary:
  │   │   ├─ 读取二进制帧
  │   │   ├─ 解析帧头 (requestId + chunkIndex)
  │   │   └─ 写入对应 DownloadStream
  │   │
  │   └─ if MessageType == Close:
  │       └─ 断开连接
  │
  └─ 连接断开 → 触发自动重连
```

#### 上传请求 (WriteFileAsync)

```
WriteFileAsync(path, data):
  │
  ├─ 1. 生成 requestId
  ├─ 2. 创建 TaskCompletionSource<UploadResponse>
  ├─ 3. 注册到 PendingRequests
  │
  ├─ 4. 发送 upload_request (JSON)
  │     {"type":"upload_request","requestId":"...","path":"...","fileSize":...}
  │
  ├─ 5. 等待 upload_ack (5s 超时)
  │     └─ 超时 → 抛 TimeoutException
  │
  ├─ 6. 分块发送文件数据 (Binary)
  │     ├─ 从 data Stream 读取 64KB
  │     ├─ 发送二进制帧
  │     └─ 循环直到流结束
  │
  ├─ 7. 等待 upload_complete
  │     └─ 含 fileHash 和 fileSize
  │
  ├─ 8. 从 PendingRequests 移除
  └─ 9. 返回 UploadResult
```

#### 下载请求 (ReadFileAsync)

```
ReadFileAsync(path):
  │
  ├─ 1. 生成 requestId
  ├─ 2. 创建 Pipe (System.IO.Pipelines)
  ├─ 3. 创建 DownloadContext (含 PipeWriter)
  ├─ 4. 注册到 PendingRequests
  │
  ├─ 5. 发送 download_request (JSON)
  │     {"type":"download_request","requestId":"...","path":"..."}
  │
  ├─ 6. 接收二进制帧（在 ReceiveLoop 中）
  │     ├─ 每收到一帧 → PipeWriter.WriteAsync
  │     └─ download_complete → PipeWriter.Complete
  │
  ├─ 7. 返回 PipeReader.AsStream()（流式，不缓冲完整文件）
  └─ 注意：返回的 Stream 是只读的，消费后自动关闭
```

#### 心跳与重连

```
Heartbeat:
  ├─ Timer 每 30s 触发
  ├─ 发送 {"type":"ping"}
  ├─ 期望 5s 内收到 {"type":"pong"}
  └─ 超时 → 标记断开 → 触发重连

Reconnect:
  ├─ 指数退避: 1s → 2s → 4s → 8s → ... → 30s max
  ├─ 每次重连重新认证
  ├─ 重连成功 → 重置退避
  └─ 恢复所有注册的路径前缀

Disconnect:
  ├─ 手动断开 → 发送 Close 帧
  ├─ 取消所有 PendingRequests（抛异常）
  └─ 停止心跳 Timer
```

#### 完整客户端代码骨架

```csharp
public class WsFileStorageClient : IFileStorageClient, IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private ClientWebSocket? _webSocket;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseMessage>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, DownloadContext> _downloads = new();
    private Timer? _heartbeatTimer;
    private CancellationTokenSource _disconnectCts = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // 重连策略
    private int _reconnectAttempt;
    private static readonly int[] ReconnectDelays = { 1000, 2000, 4000, 8000, 15000, 30000 };

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public event EventHandler<DisconnectEventArgs>? OnDisconnected;
    public string[] SupportedPaths { get; set; } = Array.Empty<string>();

    public WsFileStorageClient(string serverUrl, string clientId, string clientSecret)
    {
        _serverUrl = serverUrl;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task ConnectAsync(string serverUrl, string clientId, string clientSecret)
    {
        await _connectLock.WaitAsync();
        try
        {
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var token = ComputeToken(clientId, clientSecret, timestamp);
            var uri = new Uri($"{_serverUrl}/ws/connect?clientId={clientId}&token={token}&timestamp={timestamp}");

            await _webSocket.ConnectAsync(uri, _disconnectCts.Token);

            // 发送注册信息
            var registerMsg = JsonSerializer.Serialize(new
            {
                type = "register",
                supportedPaths = SupportedPaths,
                storageCapacity = GetStorageCapacity()
            });
            await SendTextAsync(registerMsg);

            _reconnectAttempt = 0;
            StartHeartbeat();
            _ = ReceiveLoopAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 64]; // 64KB
        var messageBuffer = new MemoryStream();

        try
        {
            while (_webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(buffer, _disconnectCts.Token);

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Text:
                        // 累积文本帧
                        messageBuffer.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            messageBuffer.SetLength(0);
                            await HandleTextMessage(json);
                        }
                        break;

                    case WebSocketMessageType.Binary:
                        await HandleBinaryMessage(buffer, result.Count, result.EndOfMessage);
                        break;

                    case WebSocketMessageType.Close:
                        await HandleClose();
                        return;
                }
            }
        }
        catch (WebSocketException)
        {
            // 连接断开
        }
        finally
        {
            await OnConnectionLost();
        }
    }

    private async Task HandleTextMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "upload_request":
                await HandleUploadRequest(doc);
                break;
            case "download_data":
                // 下载数据由二进制帧处理
                break;
            case "download_complete":
            case "upload_complete":
            case "delete_complete":
                // 请求完成响应
                var requestId = doc.RootElement.GetProperty("requestId").GetString();
                if (requestId != null && _pendingRequests.TryRemove(requestId, out var tcs))
                {
                    tcs.TrySetResult(new ResponseMessage(type, doc));
                }
                break;
            case "pong":
                // 心跳响应，无需处理
                break;
            case "error":
                var errRequestId = doc.RootElement.GetProperty("requestId").GetString();
                if (errRequestId != null && _pendingRequests.TryRemove(errRequestId, out var errTcs))
                {
                    errTcs.TrySetException(new WsClientException(doc.RootElement.GetProperty("errorMessage").GetString()));
                }
                break;
        }
    }

    private async Task HandleBinaryMessage(byte[] buffer, int count, bool endOfMessage)
    {
        // 解析帧头：requestId (16 bytes GUID) + chunkIndex (4 bytes int)
        var requestIdBytes = buffer[..16];
        var requestId = new Guid(requestIdBytes).ToString();

        if (_downloads.TryGetValue(requestId, out var ctx))
        {
            await ctx.PipeWriter.WriteAsync(buffer[20..count]);
            if (endOfMessage && chunkIndex == ctx.TotalChunks - 1)
            {
                ctx.PipeWriter.Complete();
                _downloads.TryRemove(requestId, out _);
            }
        }
    }

    public async Task<Stream> ReadFileAsync(string path)
    {
        var requestId = Guid.NewGuid().ToString();
        var pipe = new Pipe();
        var ctx = new DownloadContext { PipeWriter = pipe.Writer, TotalChunks = 0 };
        _downloads[requestId] = ctx;

        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestId] = tcs;

        await SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "download_request",
            requestId,
            path
        }));

        // 等待 download_complete 确认
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return pipe.Reader.AsStream();
    }

    public async Task WriteFileAsync(string path, Stream data)
    {
        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestId] = tcs;

        // 发送上传请求
        await SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "upload_request",
            requestId,
            path,
            fileSize = data.Length
        }));

        // 等待 ACK
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 分块发送文件数据
        var buffer = new byte[1024 * 64]; // 64KB
        int chunkIndex = 0;
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer)) > 0)
        {
            var frame = BuildBinaryFrame(requestId, chunkIndex, -1, buffer, bytesRead);
            await _webSocket.SendAsync(frame, WebSocketMessageType.Binary, true, _disconnectCts.Token);
            chunkIndex++;
        }

        // 等待上传完成确认
        var completeTcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestId] = completeTcs;
        await completeTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task DeleteFileAsync(string path)
    {
        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestId] = tcs;

        await SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "delete_request",
            requestId,
            path
        }));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task ReconnectAsync()
    {
        var delay = ReconnectDelays[Math.Min(_reconnectAttempt, ReconnectDelays.Length - 1)];
        _reconnectAttempt++;
        await Task.Delay(delay);
        await ConnectAsync(_serverUrl, _clientId, _clientSecret);
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer?.Dispose();
        _disconnectCts.Cancel();
        if (_webSocket != null)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down", CancellationToken.None);
            _webSocket.Dispose();
        }
    }
}
```

## 客户端部署

### 独立控制台应用

```bash
# 安装
dotnet tool install -g FileUploadServer.WsClient

# 运行
fileupload-wsclient --server wss://fileserver.example.com \
                    --client-id node-1 \
                    --client-secret sk-wsc-xxxxx \
                    --storage-path /mnt/storage/files \
                    --paths /public/*,/shared/*
```

### Docker 部署

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:10.0
COPY publish/ /app/
VOLUME /data/files
CMD ["dotnet", "FileUploadServer.WsClient.dll",
     "--server", "wss://fileserver.example.com",
     "--storage-path", "/data/files"]
```

### Kubernetes Sidecar

```yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: fileupload-server
          image: fileupload-server:latest
        - name: ws-client
          image: fileupload-ws-client:latest
          env:
            - name: WS_SERVER_URL
              value: "ws://localhost:5005"
            - name: CLIENT_ID
              valueFrom:
                fieldRef:
                  fieldPath: "metadata.name"
            - name: STORAGE_PATH
              value: "/data/files"
          volumeMounts:
            - mountPath: /data/files
              name: file-storage
```

## 与加密的关系

WS 客户端存储的是**加密后的数据**还是**明文数据**？

| 场景 | 存储内容 | 说明 |
|------|----------|------|
| 网关模式 | 加密数据 | 服务端加密后转发，客户端存密文 |
| 客户端加密 | 明文数据 | 客户端自行负责加密（S3 服务端加密等） |
| 混合 | 取决于配置 | StorageStrategy 决定 |

推荐：**服务端加密 + 密文转发**，保持加密逻辑集中，客户端仅做存储。

## 测试策略

### 单元测试（Mock WebSocket）

```csharp
// 使用 WebSocketServer 模拟器
// 或用 MemoryWebSocket（System.Net.WebSockets 提供）
public class WsFileStorageClientTests
{
    [Fact]
    public async Task Upload_File_SendsCorrectMessages()
    {
        // Arrange: 创建 WebSocket server 模拟
        // Act: 客户端上传文件
        // Assert: 验证控制消息和二进制帧顺序正确
    }

    [Fact]
    public async Task Download_File_ReturnsCorrectData()
    {
        // Arrange: server 发送预定义的二进制帧
        // Act: 客户端下载
        // Assert: 返回的数据与 server 发送的一致
    }

    [Fact]
    public async Task Reconnect_AfterDisconnect_ResumesOperation()
    {
        // Arrange: 客户端连接后手动断开
        // Act: 等待重连
        // Assert: 重连成功，可继续操作
    }
}
```

### 集成测试

```csharp
// 启动真实 ASP.NET Core 服务 + WebSocket 中间件
// 启动客户端连接到服务
// 测试完整的上传-下载-删除流程
```
