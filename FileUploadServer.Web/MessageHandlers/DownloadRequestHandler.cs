using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;

namespace FileUploadServer.Web.MessageHandlers;

/// <summary>
/// 处理下载请求消息（download_request）。
/// 流程：接收 download_request → 读取文件 → 分块发送 download_data（二进制帧） → 发送 download_complete
/// 使用 Pipe/PipeWriter 实现流式处理，不缓冲完整文件。
/// </summary>
public class DownloadRequestHandler : IMessageHandler
{
    private readonly IStorageStrategyFactory _strategyFactory;
    private readonly ILogger<DownloadRequestHandler> _logger;

    /// <summary>默认分块大小（64KB）。</summary>
    private const int ChunkSize = 64 * 1024;

    public string MessageType => "download_request";

    public DownloadRequestHandler(
        IStorageStrategyFactory strategyFactory,
        ILogger<DownloadRequestHandler> logger)
    {
        _strategyFactory = strategyFactory;
        _logger = logger;
    }

    public async Task HandleAsync(
        string clientId,
        JsonDocument message,
        byte[]? payload,
        object connection)
    {
        var wsClientConnection = connection as WsClientConnection;
        if (wsClientConnection?.WebSocket == null)
        {
            _logger.LogError("Invalid connection object for client {ClientId}", clientId);
            return;
        }
        var webSocket = wsClientConnection.WebSocket;

        var root = message.RootElement;

        var requestId = root.GetProperty("requestId").GetString()!;
        var path = root.GetProperty("path").GetString()!;

        _logger.LogInformation(
            "Download request from client {ClientId}: requestId={RequestId}, path={Path}",
            clientId, requestId, path);

        // 1. 路径安全检查
        if (!IsValidPath(path))
        {
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "download_error",
                requestId,
                code = 4002,
                message = "Invalid or unsafe path"
            });
            return;
        }

        // 2. 通过存储策略读取文件流
        Stream fileStream;
        try
        {
            var strategy = _strategyFactory.GetStrategy(path);
            fileStream = await strategy.ReadAsync(path);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Download request for non-existent file: {Path}", path);
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "download_error",
                requestId,
                code = 4001,
                message = "File not found"
            });
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file {Path} for download (request {RequestId})", path, requestId);
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "download_error",
                requestId,
                code = 5000,
                message = $"Failed to read file: {ex.Message}"
            });
            return;
        }

        // 3. 使用 Pipe 实现流式传输
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: ChunkSize * 4,
            resumeWriterThreshold: ChunkSize * 2,
            readerScheduler: PipeScheduler.Inline));

        // 计算总分块数
        var totalFileSize = fileStream.Length;
        var totalChunks = totalFileSize > 0
            ? (int)Math.Ceiling((double)totalFileSize / ChunkSize)
            : 1;
        var requestGuid = Guid.Parse(requestId);

        // 写入任务：从文件流读取数据写入 Pipe
        var writerTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new byte[ChunkSize];
                int bytesRead;
                uint chunkIndex = 0;

                while ((bytesRead = await fileStream.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    var data = bytesRead == buffer.Length
                        ? buffer
                        : buffer[..bytesRead];

                    // 直接发送二进制帧到 WebSocket
                    await WebSocketHandlerMiddleware.SendBinaryAsync(
                        webSocket, requestGuid, chunkIndex, (uint)totalChunks, data);

                    chunkIndex++;
                }

                await pipe.Writer.CompleteAsync();
            }
            catch (Exception ex)
            {
                await pipe.Writer.CompleteAsync(ex);
            }
            finally
            {
                await fileStream.DisposeAsync();
            }
        }, CancellationToken.None);

        // 4. 等待写入完成
        try
        {
            await writerTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during streaming download {RequestId}", requestId);
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "download_error",
                requestId,
                code = 5000,
                message = $"Stream error: {ex.Message}"
            });
            return;
        }

        // 5. 发送 download_complete
        await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
        {
            type = "download_complete",
            requestId,
            totalChunks,
            fileSize = totalFileSize
        });

        _logger.LogInformation(
            "Download completed: requestId={RequestId}, path={Path}, chunks={Chunks}, size={Size}",
            requestId, path, totalChunks, totalFileSize);
    }

    /// <summary>
    /// 路径安全检查。
    /// </summary>
    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!path.StartsWith('/')) return false;
        if (path.Contains("..")) return false;
        if (path.Contains('\0')) return false;
        if (path.Length > 1024) return false;
        return true;
    }
}
