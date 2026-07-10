using System.Text.Json;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;

namespace FileUploadServer.Web.MessageHandlers;

/// <summary>
/// 处理心跳消息（ping / pong）。
/// 收到 ping → 回复 pong。
/// 心跳更新时间戳已在中间件中处理，此处理器主要处理需要显式回复的场景。
/// </summary>
public class PingPongHandler : IMessageHandler
{
    private readonly ILogger<PingPongHandler> _logger;

    public string MessageType => "ping";

    public PingPongHandler(ILogger<PingPongHandler> logger)
    {
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
        var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
        var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : (long?)null;

        _logger.LogDebug("Ping from client {ClientId} (requestId: {RequestId})", clientId, requestId);

        // 回复 pong
        var pongMessage = new Dictionary<string, object?>
        {
            ["type"] = "pong",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (requestId != null)
        {
            pongMessage["requestId"] = requestId;
        }

        if (timestamp.HasValue)
        {
            pongMessage["echoTimestamp"] = timestamp.Value;
        }

        await WebSocketHandlerMiddleware.SendJsonAsync(webSocket, pongMessage);
    }
}
