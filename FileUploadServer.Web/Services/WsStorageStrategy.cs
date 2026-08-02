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
    /// 流程：发送 download_request → 接收二进制数据帧（经由 PendingDownloadStreams）→ 等待 download_complete。
    /// </summary>
    public async Task<Stream> ReadAsync(string path)
    {
        if (!_connectionManager.TryPickClientForPath(path, out var client))
        {
            throw new InvalidOperationException($"No available WS client for path: {path}");
        }

        var requestId = Guid.NewGuid().ToString();
        var ws = client.WebSocket;
        var stream = new MemoryStream();

        _logger.LogDebug("WS storage read: client={ClientId}, path={Path}, requestId={RequestId}",
            client.ClientId, path, requestId);

        // 注册下载流，让中间件把二进制帧写入这里
        WebSocketHandlerMiddleware.PendingDownloadStreams[requestId] = stream;

        // 发送 download_request
        await WebSocketHandlerMiddleware.SendJsonAsync(ws, new
        {
            type = "download_request",
            requestId,
            path
        });

        // 等待 download_complete（通过 PendingResponses）
        var completeTcs = new TaskCompletionSource<JsonDocument>();
        WebSocketHandlerMiddleware.PendingResponses[requestId] = completeTcs;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));

        try
        {
            cts.Token.Register(() => completeTcs.TrySetCanceled());
            var completeDoc = await completeTcs.Task;
            var responseType = completeDoc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : "";

            if (responseType == "download_error")
            {
                var errMsg = completeDoc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "Unknown error";
                throw new InvalidOperationException($"WS download failed: {errMsg}");
            }
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException("Timeout waiting for download from WS client");
        }
        finally
        {
            WebSocketHandlerMiddleware.PendingDownloadStreams.TryRemove(requestId, out _);
            WebSocketHandlerMiddleware.PendingResponses.TryRemove(requestId, out _);
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// 通过 WebSocket 将文件写入远程客户端。
    /// 流程：发送 upload_request → 等待 upload_ack → 分块发送二进制数据 → 等待 upload_complete。
    /// 使用 PendingResponses 字典避免与中间件 ReceiveLoop 竞争读取 WebSocket。
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

        var fileSize = data.Length;
        const int chunkSize = 64 * 1024;
        var totalChunks = fileSize > 0
            ? (int)Math.Ceiling((double)fileSize / chunkSize)
            : 1;

        // 发送 upload_request
        await WebSocketHandlerMiddleware.SendJsonAsync(ws, new
        {
            type = "upload_request",
            requestId,
            path,
            fileName = Path.GetFileName(path),
            fileSize
        });

        // 等待 upload_ack（通过 PendingResponses，不直接读 WebSocket）
        var ackTcs = new TaskCompletionSource<JsonDocument>();
        WebSocketHandlerMiddleware.PendingResponses[requestId] = ackTcs;
        using var ackCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            ackCts.Token.Register(() => ackTcs.TrySetCanceled());
            var ackDoc = await ackTcs.Task;
            var ackType = ackDoc.RootElement.TryGetProperty("type", out var at) ? at.GetString() : "";
            if (ackType != "upload_ack")
                throw new InvalidOperationException($"Expected upload_ack, got: {ackType}");
        }
        catch (TaskCanceledException)
        {
            WebSocketHandlerMiddleware.PendingResponses.TryRemove(requestId, out _);
            throw new TimeoutException("Timeout waiting for upload_ack from WS client");
        }

        // 分块发送文件数据
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

        // 发送 upload_complete 信号
        await WebSocketHandlerMiddleware.SendJsonAsync(ws, new
        {
            type = "upload_complete",
            requestId
        });

        // 等待 upload_complete 响应（通过 PendingResponses）
        var completeTcs = new TaskCompletionSource<JsonDocument>();
        WebSocketHandlerMiddleware.PendingResponses[requestId] = completeTcs;
        using var completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));
        try
        {
            completeCts.Token.Register(() => completeTcs.TrySetCanceled());
            var completeDoc = await completeTcs.Task;
            var responseType = completeDoc.RootElement.TryGetProperty("type", out var ctp) ? ctp.GetString() : "";
            if (responseType == "upload_error")
            {
                var errMsg = completeDoc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "Unknown error";
                throw new InvalidOperationException($"WS upload failed: {errMsg}");
            }
        }
        catch (TaskCanceledException)
        {
            WebSocketHandlerMiddleware.PendingResponses.TryRemove(requestId, out _);
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

        // 等待响应（通过 PendingResponses，不直接读 WebSocket）
        var delTcs = new TaskCompletionSource<JsonDocument>();
        WebSocketHandlerMiddleware.PendingResponses[requestId] = delTcs;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(OperationTimeoutSeconds));
        try
        {
            cts.Token.Register(() => delTcs.TrySetCanceled());
            var delDoc = await delTcs.Task;
            var responseType = delDoc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : "";
            if (responseType == "delete_error")
            {
                var errMsg = delDoc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "Unknown error";
                throw new InvalidOperationException($"WS delete failed: {errMsg}");
            }
        }
        catch (TaskCanceledException)
        {
            WebSocketHandlerMiddleware.PendingResponses.TryRemove(requestId, out _);
            throw new TimeoutException("Timeout waiting for delete response from WS client");
        }
    }
}
