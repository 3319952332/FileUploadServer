using System.Text.Json;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.MessageHandlers;

/// <summary>
/// 处理删除请求消息（delete_request）。
/// 流程：接收 delete_request → 删除文件 → 发送 delete_complete
/// </summary>
public class DeleteRequestHandler : IMessageHandler
{
    private readonly AppDbContext _dbContext;
    private readonly IStorageStrategyFactory _strategyFactory;
    private readonly ILogger<DeleteRequestHandler> _logger;

    public string MessageType => "delete_request";

    public DeleteRequestHandler(
        AppDbContext dbContext,
        IStorageStrategyFactory strategyFactory,
        ILogger<DeleteRequestHandler> logger)
    {
        _dbContext = dbContext;
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
            "Delete request from client {ClientId}: requestId={RequestId}, path={Path}",
            clientId, requestId, path);

        // 1. 路径安全检查
        if (!IsValidPath(path))
        {
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "delete_error",
                requestId,
                code = 4002,
                message = "Invalid or unsafe path"
            });
            return;
        }

        // 2. 查找 FileLocation 记录
        var fileLocation = await _dbContext.Set<FileLocation>()
            .FirstOrDefaultAsync(fl => fl.FilePath == path && fl.ClientId == clientId, CancellationToken.None);

        if (fileLocation == null)
        {
            _logger.LogWarning("Delete request for unknown file: {Path} from client {ClientId}", path, clientId);
            // 仍然尝试从存储中删除文件
        }

        // 3. 通过存储策略删除文件
        try
        {
            var strategy = _strategyFactory.GetStrategy(path);
            await strategy.DeleteAsync(path);

            _logger.LogInformation("File deleted: {Path} (client: {ClientId})", path, clientId);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Delete request for non-existent file: {Path}", path);
            // 继续执行，删除 FileLocation 记录
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file {Path} (request {RequestId})", path, requestId);
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "delete_error",
                requestId,
                code = 5000,
                message = $"Failed to delete file: {ex.Message}"
            });
            return;
        }

        // 4. 删除 FileLocation 记录
        if (fileLocation != null)
        {
            _dbContext.Set<FileLocation>().Remove(fileLocation);
            await _dbContext.SaveChangesAsync();
        }

        // 5. 发送 delete_complete
        await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
        {
            type = "delete_complete",
            requestId,
            path
        });

        _logger.LogInformation("Delete completed: requestId={RequestId}, path={Path}", requestId, path);
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
