using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Web.Services;

namespace FileUploadServer.Web.Middleware;

/// <summary>
/// WebSocket 升级与消息分发中间件。
/// 拦截 /ws/connect 路径，验证客户端身份，升级 HTTP 到 WebSocket，
/// 注册到连接池，并启动消息接收循环。
/// </summary>
public class WebSocketHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WebSocketHandlerMiddleware> _logger;

    /// <summary>二进制帧头固定大小：requestId(16) + chunkIndex(4 BE) + totalChunks(4 BE) = 24 bytes。</summary>
    public const int BinaryFrameHeaderSize = 24;

    /// <summary>JSON 消息接收的最大大小（1MB）。</summary>
    private const int MaxJsonMessageSize = 1 * 1024 * 1024;

    /// <summary>默认文件块大小（64KB）。</summary>
    private const int DefaultChunkSize = 64 * 1024;

    /// <summary>待处理上传映射表（requestId → PendingUpload）。</summary>
    private static readonly ConcurrentDictionary<string, PendingUpload> PendingUploads = new();

    /// <summary>WS 策略响应等待映射表（requestId → TaskCompletionSource）。</summary>
    /// 用于 WsStorageStrategy 等待 WS 客户端的响应消息，避免与中间件 ReceiveLoop 竞争读取。
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> PendingResponses = new();

    /// <summary>待处理下载流映射表（requestId → MemoryStream）。</summary>
    /// 用于 WsStorageStrategy.ReadAsync 接收二进制帧数据，避免与中间件 ReceiveLoop 竞争。
    public static readonly ConcurrentDictionary<string, MemoryStream> PendingDownloadStreams = new();

    public WebSocketHandlerMiddleware(RequestDelegate next, ILogger<WebSocketHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        WsConnectionManager connectionManager,
        WsClientAuthService authService,
        IEnumerable<IMessageHandler> messageHandlers)
    {
        if (!context.Request.Path.StartsWithSegments("/ws/connect", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var clientId = context.Request.Query["clientId"].FirstOrDefault();
        var token = context.Request.Query["token"].FirstOrDefault();
        var timestampStr = context.Request.Query["timestamp"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(timestampStr))
        {
            _logger.LogWarning("WebSocket connect missing required params (clientId, token, timestamp)");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing required parameters: clientId, token, timestamp");
            return;
        }

        if (!long.TryParse(timestampStr, out var timestamp))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid timestamp format");
            return;
        }

        var isAuthenticated = await authService.ValidateConnectionAsync(clientId, token, timestamp);
        if (!isAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Authentication failed");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket request");
            return;
        }

        WebSocket webSocket;
        try
        {
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept WebSocket connection for client {ClientId}", clientId);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var pathPrefixesStr = context.Request.Query["prefixes"].FirstOrDefault();
        var pathPrefixes = string.IsNullOrEmpty(pathPrefixesStr)
            ? Array.Empty<string>()
            : pathPrefixesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await connectionManager.RegisterConnectionAsync(clientId, webSocket, pathPrefixes);
        connectionManager.UpdateHeartbeat(clientId);

        _logger.LogInformation("WebSocket client {ClientId} connected (prefixes: {Prefixes})",
            clientId, string.Join(", ", pathPrefixes));

        var handlerDict = new Dictionary<string, IMessageHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in messageHandlers)
        {
            if (!string.IsNullOrEmpty(handler.MessageType))
            {
                handlerDict[handler.MessageType] = handler;
                _logger.LogDebug("Registered handler for message type '{MessageType}'", handler.MessageType);
            }
        }

        // 后台处理器任务跟踪（fire-and-forget 的 handler 任务）
        var backgroundTasks = new List<Task>();

        try
        {
            await ReceiveLoopAsync(clientId, webSocket, connectionManager, handlerDict, backgroundTasks);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for client {ClientId}: {Message}", clientId, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket receive loop cancelled for client {ClientId}", clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in WebSocket receive loop for client {ClientId}", clientId);
        }
        finally
        {
            // 等待后台处理器完成（最多 5 秒）
            try
            {
                if (backgroundTasks.Count > 0)
                {
                    await Task.WhenAll(backgroundTasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Some background handler tasks did not complete for client {ClientId}", clientId);
            }

            await connectionManager.UnregisterConnectionAsync(clientId);

            // 清理该客户端相关的待处理上传
            var pendingKeys = PendingUploads
                .Where(kvp => kvp.Value.ClientId == clientId)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in pendingKeys)
            {
                if (PendingUploads.TryRemove(key, out var pending))
                {
                    pending.Cancel();
                }
            }

            _logger.LogInformation("WebSocket client {ClientId} disconnected", clientId);
        }
    }

    /// <summary>
    /// WebSocket 消息接收主循环。
    /// 文本帧（控制消息）→ 后台分派到 handler
    /// 二进制帧（文件数据）→ 直接写入待处理上传
    /// </summary>
    private async Task ReceiveLoopAsync(
        string clientId,
        WebSocket webSocket,
        WsConnectionManager connectionManager,
        Dictionary<string, IMessageHandler> handlerDict,
        List<Task> backgroundTasks)
    {
        var buffer = new byte[DefaultChunkSize + BinaryFrameHeaderSize];
        var jsonAccumulator = new List<byte>();

        while (webSocket.State == WebSocketState.Open && !connectionManager.GetConnection(clientId)?.DisconnectCts.IsCancellationRequested == true)
        {
            WebSocketReceiveResult receiveResult;
            try
            {
                receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                _logger.LogWarning("Connection closed prematurely for client {ClientId}", clientId);
                break;
            }

            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("Client {ClientId} sent close frame", clientId);
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            try
            {
                if (receiveResult.MessageType == WebSocketMessageType.Text)
                {
                    var messageBytes = AccumulateJsonMessage(buffer, receiveResult, ref jsonAccumulator);
                    if (messageBytes != null)
                    {
                        // 在后台任务中处理文本消息，避免阻塞接收循环
                        var task = ProcessTextMessageAsync(
                            clientId, webSocket, messageBytes, handlerDict, connectionManager);
                        backgroundTasks.Add(task);

                        // 清理已完成的任务引用
                        backgroundTasks.RemoveAll(t => t.IsCompleted);
                    }
                }
                else if (receiveResult.MessageType == WebSocketMessageType.Binary)
                {
                    await ProcessBinaryFrameAsync(buffer, receiveResult, clientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling message from client {ClientId}", clientId);
            }
        }
    }

    /// <summary>
    /// 累积分片的 JSON 消息，完整时返回字节数组。
    /// </summary>
    private static byte[]? AccumulateJsonMessage(
        byte[] buffer, WebSocketReceiveResult result, ref List<byte> accumulator)
    {
        if (!result.EndOfMessage)
        {
            accumulator.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
            return null;
        }

        if (accumulator.Count > 0)
        {
            accumulator.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
            var bytes = accumulator.ToArray();
            accumulator.Clear();
            return bytes;
        }

        return new ArraySegment<byte>(buffer, 0, result.Count).ToArray();
    }

    /// <summary>
    /// 在后台处理 JSON 文本消息：解析类型并分派到对应的 IMessageHandler。
    /// </summary>
    private async Task ProcessTextMessageAsync(
        string clientId,
        WebSocket webSocket,
        byte[] jsonBytes,
        Dictionary<string, IMessageHandler> handlerDict,
        WsConnectionManager connectionManager)
    {
        if (jsonBytes.Length > MaxJsonMessageSize)
        {
            _logger.LogWarning("Oversized JSON message from client {ClientId}: {Size} bytes", clientId, jsonBytes.Length);
            return;
        }

        using var doc = JsonDocument.Parse(jsonBytes);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            _logger.LogWarning("Message from client {ClientId} missing 'type' field", clientId);
            return;
        }

        var messageType = typeProp.GetString() ?? string.Empty;
        var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;

        _logger.LogDebug("Received message type '{MessageType}' (requestId: {RequestId}) from client {ClientId}",
            messageType, requestId, clientId);

        // 心跳更新（ping 和 pong 都更新心跳时间戳）
        if (messageType == "ping" || messageType == "pong")
        {
            connectionManager.UpdateHeartbeat(clientId);
        }

        // WS策略响应消息：完成等待中的 TaskCompletionSource
        if (requestId != null && PendingResponses.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(doc);
            return;
        }

        // 获取连接对象传递给 handler
        var wsConnection = connectionManager.GetConnection(clientId);
        if (wsConnection == null)
        {
            _logger.LogWarning("Connection not found for client {ClientId}, cannot dispatch message", clientId);
            return;
        }

        if (handlerDict.TryGetValue(messageType, out var handler))
        {
            try
            {
                await handler.HandleAsync(clientId, doc, null, wsConnection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler '{MessageType}' failed for client {ClientId}", messageType, clientId);
                await SendJsonAsync(wsConnection.WebSocket, new
                {
                    type = "error",
                    requestId,
                    code = 5000,
                    message = $"Handler error: {ex.Message}"
                });
            }
        }
        else
        {
            _logger.LogWarning("No handler for message type '{MessageType}' from client {ClientId}",
                messageType, clientId);
            await SendJsonAsync(wsConnection.WebSocket, new
            {
                type = "error",
                requestId,
                code = 5002,
                message = $"Unknown message type: {messageType}"
            });
        }
    }

    /// <summary>
    /// 处理二进制帧：解析帧头并写入对应的待处理上传。
    /// </summary>
    private async Task ProcessBinaryFrameAsync(
        byte[] buffer, WebSocketReceiveResult receiveResult, string clientId)
    {
        // 二进制帧格式：[requestId (16B)] [chunkIndex (4B BE)] [totalChunks (4B BE)] [payload]
        if (!receiveResult.EndOfMessage)
        {
            _logger.LogWarning("Fragmented binary frame from client {ClientId}, not yet supported", clientId);
            return;
        }

        var frameData = new ArraySegment<byte>(buffer, 0, receiveResult.Count);
        if (frameData.Count < BinaryFrameHeaderSize)
        {
            _logger.LogWarning("Binary frame too small ({Size} bytes) from client {ClientId}", frameData.Count, clientId);
            return;
        }

        var requestId = new Guid(frameData.AsSpan(0, 16));
        var chunkIndex = ReadBigEndianUInt32(frameData.AsSpan(16, 4));
        var totalChunks = ReadBigEndianUInt32(frameData.AsSpan(20, 4));

        var payload = frameData.Count > BinaryFrameHeaderSize
            ? frameData[BinaryFrameHeaderSize..].ToArray()
            : Array.Empty<byte>();

        var requestIdStr = requestId.ToString();

        // 优先检查 PendingDownloadStreams（WS 下载响应）
        if (PendingDownloadStreams.TryGetValue(requestIdStr, out var downloadStream))
        {
            await downloadStream.WriteAsync(payload);
        }
        else if (PendingUploads.TryGetValue(requestIdStr, out var pending))
        {
            await pending.WriteChunkAsync(chunkIndex, payload, totalChunks);
        }
        else
        {
            _logger.LogWarning("Binary frame for unknown request {RequestId} from client {ClientId}",
                requestIdStr, clientId);
        }
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> bytes)
    {
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    /// <summary>发送 JSON 对象作为文本帧到 WebSocket。</summary>
    public static async Task SendJsonAsync(WebSocket webSocket, object message)
    {
        if (webSocket.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>发送二进制数据帧到 WebSocket。</summary>
    public static async Task SendBinaryAsync(
        WebSocket webSocket, Guid requestId, uint chunkIndex, uint totalChunks, byte[] data)
    {
        if (webSocket.State != WebSocketState.Open) return;
        var frame = BuildBinaryFrame(requestId, chunkIndex, totalChunks, data);
        await webSocket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    /// <summary>构建二进制帧：[requestId(16)] [chunkIndex(4 BE)] [totalChunks(4 BE)] [data]。</summary>
    public static byte[] BuildBinaryFrame(Guid requestId, uint chunkIndex, uint totalChunks, byte[] data)
    {
        var frame = new byte[BinaryFrameHeaderSize + (data?.Length ?? 0)];
        requestId.TryWriteBytes(frame.AsSpan(0, 16));

        var ci = BitConverter.GetBytes(chunkIndex);
        var tc = BitConverter.GetBytes(totalChunks);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(ci);
            Array.Reverse(tc);
        }
        ci.CopyTo(frame, 16);
        tc.CopyTo(frame, 20);

        data?.CopyTo(frame, BinaryFrameHeaderSize);
        return frame;
    }

    /// <summary>创建并注册一个待处理上传上下文。</summary>
    /// <summary>创建并注册一个待处理上传上下文。</summary>
    public static PendingUpload RegisterPendingUpload(string clientId, string requestId, int totalChunks, Stream writeStream)
    {
        var pending = new PendingUpload(clientId, requestId, totalChunks, writeStream);
        PendingUploads[requestId] = pending;
        return pending;
    }

    /// <summary>移除并清理待处理上传上下文。</summary>
    public static void RemovePendingUpload(string requestId)
    {
        PendingUploads.TryRemove(requestId, out _);
    }
}

/// <summary>
/// 待处理上传上下文，用于接收分块数据并写入目标流。
/// 支持通过 TaskCompletionSource 等待完成。
/// </summary>
public class PendingUpload
{
    private readonly Stream _writeStream;
    private readonly int _expectedTotalChunks;
    private int _receivedChunks;
    private readonly TaskCompletionSource<bool> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private bool _completed;

    public string ClientId { get; }
    public string RequestId { get; }
    public int ReceivedChunks => _receivedChunks;
    public bool IsComplete => _completed;

    /// <summary>等待上传完成的任务。</summary>
    public Task<bool> CompletionTask => _completionSource.Task;

    public PendingUpload(string clientId, string requestId, int totalChunks, Stream writeStream)
    {
        ClientId = clientId;
        RequestId = requestId;
        _expectedTotalChunks = totalChunks;
        _writeStream = writeStream;
    }

    public async Task WriteChunkAsync(uint chunkIndex, byte[] data, uint totalChunks)
    {
        int receivedCount;
        lock (_lock)
        {
            _receivedChunks++;
            receivedCount = _receivedChunks;
        }

        await _writeStream.WriteAsync(data);

        if (receivedCount >= _expectedTotalChunks || receivedCount >= totalChunks)
        {
            Complete();
        }
    }

    /// <summary>标记上传完成，通知等待者。</summary>
    public void Complete()
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
        }

        try { _writeStream.Dispose(); } catch { }
        WebSocketHandlerMiddleware.RemovePendingUpload(RequestId);
        _completionSource.TrySetResult(true);
    }

    /// <summary>取消上传。</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
        }

        try { _writeStream.Dispose(); } catch { }
        _completionSource.TrySetResult(false);
    }

    /// <summary>等待上传完成，带超时。</summary>
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
    {
        var completedTask = await Task.WhenAny(CompletionTask, Task.Delay(timeout));
        return completedTask == CompletionTask && CompletionTask.Result;
    }
}
