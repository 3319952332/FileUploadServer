using System.Security.Cryptography;
using System.Text.Json;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.MessageHandlers;

/// <summary>
/// 处理上传请求消息（upload_request）。
/// 流程：接收 upload_request → 发送 upload_ack → 等待二进制数据块 → 存储文件 → 发送 upload_complete
/// </summary>
public class UploadRequestHandler : IMessageHandler
{
    private readonly AppDbContext _dbContext;
    private readonly IStorageStrategy _storageStrategy;
    private readonly ILogger<UploadRequestHandler> _logger;

    /// <summary>等待 ACK 的超时时间（秒）。</summary>
    private const int AckTimeoutSeconds = 5;

    /// <summary>上传完成的超时时间（秒）。</summary>
    private const int UploadTimeoutSeconds = 300;

    /// <summary>默认分块大小（64KB）。</summary>
    private const int ChunkSize = 64 * 1024;

    public string MessageType => "upload_request";

    public UploadRequestHandler(
        AppDbContext dbContext,
        IStorageStrategyFactory strategyFactory,
        ILogger<UploadRequestHandler> logger)
    {
        _dbContext = dbContext;
        _storageStrategy = strategyFactory.GetDefaultStrategy();
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
        var fileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
        var fileSize = root.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0L;

        _logger.LogInformation(
            "Upload request from client {ClientId}: requestId={RequestId}, path={Path}, fileName={FileName}, fileSize={FileSize}",
            clientId, requestId, path, fileName, fileSize);

        // 1. 路径安全检查
        if (!IsValidPath(path))
        {
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "upload_error",
                requestId,
                code = 4002,
                message = "Invalid or unsafe path"
            });
            return;
        }

        // 2. 计算分块数
        var totalChunks = fileSize > 0
            ? (int)Math.Ceiling((double)fileSize / ChunkSize)
            : 1;

        // 3. 创建临时文件接收数据
        var tempDir = Path.Combine(Path.GetTempPath(), "fusp_uploads");
        Directory.CreateDirectory(tempDir);
        var tempFilePath = Path.Combine(tempDir, $"{requestId}_{Guid.NewGuid():N}");
        var fileStream = new FileStream(
            tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
            ChunkSize, FileOptions.SequentialScan);

        // 4. 注册待处理上传
        var pending = WebSocketHandlerMiddleware.RegisterPendingUpload(
            clientId, requestId, totalChunks, fileStream);

        // 5. 发送 upload_ack
        await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
        {
            type = "upload_ack",
            requestId,
            totalChunks,
            chunkSize = ChunkSize
        });

        // 6. 等待上传完成（带超时）
        var completed = await pending.WaitForCompletionAsync(TimeSpan.FromSeconds(UploadTimeoutSeconds));

        if (!completed)
        {
            _logger.LogWarning("Upload timeout for request {RequestId} from client {ClientId}", requestId, clientId);
            pending.Cancel();
            try { File.Delete(tempFilePath); } catch { }

            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "upload_error",
                requestId,
                code = 4006,
                message = "Upload timeout: did not receive all chunks"
            });
            return;
        }

        // 7. 计算文件哈希
        string fileHash;
        try
        {
            fileHash = await ComputeFileHashAsync(tempFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute hash for upload {RequestId}", requestId);
            try { File.Delete(tempFilePath); } catch { }
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "upload_error",
                requestId,
                code = 5000,
                message = "Failed to process uploaded file"
            });
            return;
        }

        // 8. 通过存储策略保存文件
        try
        {
            await using var readStream = File.OpenRead(tempFilePath);
            await _storageStrategy.WriteAsync(path, readStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store file {Path} from upload {RequestId}", path, requestId);
            try { File.Delete(tempFilePath); } catch { }
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "upload_error",
                requestId,
                code = 5000,
                message = $"Failed to store file: {ex.Message}"
            });
            return;
        }

        // 9. 记录 FileLocation（如果使用 WS 存储策略，参数中的 clientId 是来源客户端）
        try
        {
            var fileLocation = new FileLocation
            {
                Id = Guid.NewGuid(),
                FilePath = path,
                FileName = fileName,
                FileSize = fileSize,
                FileHash = fileHash,
                ClientId = clientId,
                ApiKeyId = 0, // WS 客户端上传暂不关联 API Key
                IsPublic = false,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.Set<FileLocation>().Add(fileLocation);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save FileLocation for upload {RequestId}, file was stored", requestId);
        }

        // 10. 清理临时文件
        try { File.Delete(tempFilePath); } catch { }

        // 11. 发送 upload_complete
        await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
        {
            type = "upload_complete",
            requestId,
            fileHash,
            fileSize,
            path
        });

        _logger.LogInformation(
            "Upload completed: requestId={RequestId}, path={Path}, hash={Hash}, size={Size}",
            requestId, path, fileHash, fileSize);
    }

    /// <summary>
    /// 计算文件的 SHA256 哈希。
    /// </summary>
    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
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
