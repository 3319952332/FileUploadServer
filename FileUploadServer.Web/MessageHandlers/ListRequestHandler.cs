using System.Text.Json;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.MessageHandlers;

/// <summary>
/// 处理列表请求消息（list_request）。
/// 流程：接收 list_request → 列出文件 → 发送 list_response（JSON）
/// </summary>
public class ListRequestHandler : IMessageHandler
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ListRequestHandler> _logger;

    public string MessageType => "list_request";

    public ListRequestHandler(
        AppDbContext dbContext,
        ILogger<ListRequestHandler> logger)
    {
        _dbContext = dbContext;
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
        var pathPrefix = root.TryGetProperty("pathPrefix", out var pp)
            ? pp.GetString()
            : "/";
        var includePublic = root.TryGetProperty("includePublic", out var ip)
            ? ip.GetBoolean()
            : false;
        var skip = root.TryGetProperty("skip", out var s)
            ? s.GetInt32()
            : 0;
        var take = root.TryGetProperty("take", out var t)
            ? Math.Min(t.GetInt32(), 1000)
            : 100;

        _logger.LogInformation(
            "List request from client {ClientId}: requestId={RequestId}, pathPrefix={PathPrefix}, skip={Skip}, take={Take}",
            clientId, requestId, pathPrefix, skip, take);

        try
        {
            // 查询 FileLocation 记录
            var query = _dbContext.Set<FileLocation>().AsQueryable();

            // 按客户端 ID 和路径前缀过滤
            query = query.Where(fl =>
                fl.ClientId == clientId &&
                fl.FilePath.StartsWith(pathPrefix!));

            if (!includePublic)
            {
                query = query.Where(fl => !fl.IsPublic);
            }

            var total = await query.CountAsync(CancellationToken.None);

            var files = await query
                .OrderByDescending(fl => fl.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(fl => new
                {
                    fl.FilePath,
                    fl.FileName,
                    fl.FileSize,
                    fl.FileHash,
                    fl.CreatedAt,
                    fl.IsPublic,
                    fl.ExpiresAt
                })
                .ToListAsync(CancellationToken.None);

            // 发送 list_response
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "list_response",
                requestId,
                total,
                skip,
                take,
                files
            });

            _logger.LogInformation(
                "List completed: client={ClientId}, total={Total}, returned={Returned}",
                clientId, total, files.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list files for client {ClientId}", clientId);
            await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, new
            {
                type = "error",
                requestId,
                code = 5000,
                message = $"Failed to list files: {ex.Message}"
            });
        }
    }
}
