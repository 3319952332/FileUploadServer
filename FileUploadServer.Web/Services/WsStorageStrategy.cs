using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Web.Middleware;

namespace FileUploadServer.Web.Services;

/// <summary>
/// WS 存储策略。
/// 通过 WsConnectionManager 找到合适的客户端，发送 WebSocket 消息进行文件操作。
/// </summary>
public class WsStorageStrategy : IStorageStrategy
{
    private readonly WsConnectionManager _connectionManager;
    private readonly ILogger<WsStorageStrategy> _logger;

    /// <summary>操作超时时间（秒）。</summary>
    private const int OperationTimeoutSeconds = 30;

    public WsStorageStrategy(
        WsConnectionManager connectionManager,
        ILogger<WsStorageStrategy> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// 通过 WebSocket 从远程客户端读取文件。
    /// 流程：发送 download_request → 接收二进制数据帧 → 组合成流。
    /// </summary>
    public async Task<Stream> ReadAsync(string path)
    {
        // 1. 寻找合适的客户端
        if (!_connectionManager.TryPickClientForPath(path, out var client))
        {
            throw new InvalidOperationException($"No available WS client for path: {path}");
        }

        var requestId = Guid.NewGuid().ToString();
        var ws = client.WebSocket;
        var stream = new MemoryStream();

        _logger.LogDebug("WS storage read: client={ClientId}, path={Path}, requestId={RequestId}",
            client.ClientId, path, requestId);

        // 2. 发送 download_request
        await WebSocketHandlerMiddleware.SendJsonAsync(ws, new
        {
            type = "download_request",
            requestId,
            path
        });

        // 3. 接收响应
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));
        var buffer = new byte[65536];
        var jsonAccumulator = new List<byte>();

        while (!cts.Token.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 解析帧头获取 requestId
                if (result.Count >= WebSocketHandlerMiddleware.BinaryFrameHeaderSize)
                {
                    var frameRequestId = new Guid(buffer[..16]);

                    // 只处理匹配 requestId 的数据
                    if (frameRequestId.ToString() == requestId)
                    {
                        var payloadSize = result.Count - WebSocketHandlerMiddleware.BinaryFrameHeaderSize;
                        if (payloadSize > 0)
                        {
                            await stream.WriteAsync(buffer, WebSocketHandlerMiddleware.BinaryFrameHeaderSize,
                                payloadSize, cts.Token);
                        }
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // 累积 JSON 文本帧
                if (!result.EndOfMessage)
                {
                    jsonAccumulator.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                    continue;
                }

                if (jsonAccumulator.Count > 0)
                {
                    jsonAccumulator.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                }

                var jsonBytes = jsonAccumulator.Count > 0
                    ? jsonAccumulator.ToArray()
                    : new ArraySegment<byte>(buffer, 0, result.Count).ToArray();
                jsonAccumulator.Clear();

                using var doc = JsonDocument.Parse(jsonBytes);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                if (type == "download_complete" || type == "download_error")
                {
                    var respRequestId = root.TryGetProperty("requestId", out var rid)
                        ? rid.GetString() : null;

                    if (respRequestId == requestId)
                    {
                        if (type == "download_error")
                        {
                            var errMsg = root.TryGetProperty("message", out var msg)
                                ? msg.GetString() : "Unknown error";
                            throw new InvalidOperationException($"WS download failed: {errMsg}");
                        }
                        break; // 下载完成
                    }
                }
            }
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// 通过 WebSocket 将文件写入远程客户端。
    /// 流程：发送 upload_request → 接收 upload_ack → 分块发送二进制数据 → 等待 upload_complete。
    /// </summary>
    public async Task WriteAsync(string path, Stream data)
    {
        if (!_connectionManager.TryPickClientForPath(path, out var client))
        {
            throw new InvalidOperationException($"No available WS client for path: {path}");
        }

        var requestId = Guid.NewGuid().ToString();
        var ws = client.WebSocket;

        _logger.LogDebug("WS storage write: client={ClientId}, path={Path}, requestId={RequestId}",
            client.ClientId, path, requestId);

        // 1. 计算总分块数
        var fileSize = data.Length;
        const int chunkSize = 64 * 1024;
        var totalChunks = fileSize > 0
            ? (int)Math.Ceiling((double)fileSize / chunkSize)
            : 1;

        // 2. 发送 upload_request
        await WebSocketHandlerMiddleware.SendJsonAsync(ws, new
        {
            type = "upload_request",
            requestId,
            path,
            fileName = Path.GetFileName(path),
            fileSize
        });

        // 3. 等待 upload_ack（简单的等待模式，实际应该使用 requestId 匹配）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ackBuffer = new byte[4096];

        try
        {
            var ackResult = await ws.ReceiveAsync(new ArraySegment<byte>(ackBuffer), cts.Token);
            if (ackResult.MessageType == WebSocketMessageType.Text)
            {
                var ackJson = Encoding.UTF8.GetString(ackBuffer, 0, ackResult.Count);
                using var ackDoc = JsonDocument.Parse(ackJson);
                var ackRoot = ackDoc.RootElement;

                if (!ackRoot.TryGetProperty("type", out var ackType) || ackType.GetString() != "upload_ack")
                {
                    throw new InvalidOperationException($"Expected upload_ack, got: {ackType.GetString()}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timeout waiting for upload_ack from WS client");
        }

        // 4. 分块发送文件数据
        var requestGuid = Guid.Parse(requestId);
        var buffer = new byte[chunkSize];
        uint chunkIndex = 0;

        while (true)
        {
            var bytesRead = await data.ReadAsync(buffer);
            if (bytesRead == 0) break;

            var chunk = bytesRead == buffer.Length
                ? buffer
                : buffer[..bytesRead];

            await WebSocketHandlerMiddleware.SendBinaryAsync(
                ws, requestGuid, chunkIndex, (uint)totalChunks, chunk);

            chunkIndex++;
        }

        // 5. 等待 upload_complete
        var completeBuffer = new byte[4096];
        try
        {
            using var completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));
            var completeResult = await ws.ReceiveAsync(new ArraySegment<byte>(completeBuffer), completeCts.Token);

            if (completeResult.MessageType == WebSocketMessageType.Text)
            {
                var completeJson = Encoding.UTF8.GetString(completeBuffer, 0, completeResult.Count);
                using var completeDoc = JsonDocument.Parse(completeJson);
                var completeRoot = completeDoc.RootElement;

                if (!completeRoot.TryGetProperty("type", out var completeType)) return;
                var responseType = completeType.GetString();

                if (responseType == "upload_error")
                {
                    var errMsg = completeRoot.TryGetProperty("message", out var msg)
                        ? msg.GetString() : "Unknown error";
                    throw new InvalidOperationException($"WS upload failed: {errMsg}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timeout waiting for upload_complete from WS client");
        }
    }

    /// <summary>
    /// 通过 WebSocket 删除远程客户端上的文件。
    /// 流程：发送 delete_request → 等待 delete_complete。
    /// </summary>
    public async Task DeleteAsync(string path)
    {
        if (!_connectionManager.TryPickClientForPath(path, out var client))
        {
            _logger.LogWarning("No available WS client for deletion: {Path}", path);
            return;
        }

        var requestId = Guid.NewGuid().ToString();
        var ws = client.WebSocket;

        _logger.LogDebug("WS storage delete: client={ClientId}, path={Path}, requestId={RequestId}",
            client.ClientId, path, requestId);

        await WebSocketHandlerMiddleware.SendJsonAsync(ws, new
        {
            type = "delete_request",
            requestId,
            path
        });

        // 等待 delete_complete
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));
        var buffer = new byte[4096];

        try
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp))
                {
                    var responseType = typeProp.GetString();
                    if (responseType == "delete_error")
                    {
                        var errMsg = root.TryGetProperty("message", out var msg)
                            ? msg.GetString() : "Unknown error";
                        throw new InvalidOperationException($"WS delete failed: {errMsg}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timeout waiting for delete_complete from WS client");
        }
    }
}
