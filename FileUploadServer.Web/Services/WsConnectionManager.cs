using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace FileUploadServer.Web.Services;

/// <summary>
/// WebSocket 连接池管理器，管理所有活跃的 WS 客户端连接。
/// 维护路径前缀索引以支持快速路由，并执行心跳检测。
/// </summary>
public class WsConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, WsClientConnection> _connections = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _pathPrefixIndex = new();
    private readonly ILogger<WsConnectionManager> _logger;
    private Timer? _heartbeatTimer;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(60);
    private bool _disposed;
    private readonly object _lockObj = new();

    public WsConnectionManager(ILogger<WsConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 注册一个新的 WS 客户端连接到连接池。
    /// </summary>
    public Task<bool> RegisterConnectionAsync(string clientId, WebSocket webSocket, string[] pathPrefixes)
    {
        if (_connections.TryGetValue(clientId, out var existing))
        {
            _logger.LogWarning("Client {ClientId} already registered, replacing old connection", clientId);
            existing.DisconnectCts.Cancel();
        }

        var now = DateTime.UtcNow;
        var connection = new WsClientConnection
        {
            ClientId = clientId,
            WebSocket = webSocket,
            ConnectedAt = now,
            LastHeartbeat = now,
            SupportedPaths = pathPrefixes.ToList(),
            DisconnectCts = new CancellationTokenSource()
        };

        _connections[clientId] = connection;

        // Update path prefix index
        foreach (var prefix in pathPrefixes)
        {
            _pathPrefixIndex.AddOrUpdate(
                NormalizePrefix(prefix),
                _ => new HashSet<string> { clientId },
                (_, set) =>
                {
                    lock (_lockObj) { set.Add(clientId); }
                    return set;
                }
            );
        }

        _logger.LogInformation("Client {ClientId} registered with {PathCount} path prefixes", clientId, pathPrefixes.Length);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 从连接池注销一个 WS 客户端连接。
    /// </summary>
    public async Task UnregisterConnectionAsync(string clientId)
    {
        if (_connections.TryRemove(clientId, out var connection))
        {
            // Remove from path prefix index
            foreach (var prefix in connection.SupportedPaths)
            {
                if (_pathPrefixIndex.TryGetValue(NormalizePrefix(prefix), out var set))
                {
                    lock (_lockObj) { set.Remove(clientId); }
                    if (set.Count == 0)
                    {
                        _pathPrefixIndex.TryRemove(NormalizePrefix(prefix), out _);
                    }
                }
            }

            connection.DisconnectCts.Cancel();
            if (connection.WebSocket.State != WebSocketState.Closed &&
                connection.WebSocket.State != WebSocketState.Aborted)
            {
                try
                {
                    await connection.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Unregistered", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing WebSocket for client {ClientId}", clientId);
                }
            }

            _logger.LogInformation("Client {ClientId} unregistered", clientId);
        }
    }

    /// <summary>
    /// 根据 clientId 获取连接。
    /// </summary>
    public WsClientConnection? GetConnection(string clientId)
    {
        _connections.TryGetValue(clientId, out var connection);
        return connection;
    }

    /// <summary>
    /// 获取匹配给定文件路径的所有在线连接。
    /// 通过路径前缀匹配，返回所有前缀匹配且状态为 Open 的连接。
    /// </summary>
    public List<WsClientConnection> GetConnectionsForPath(string filePath)
    {
        var result = new List<WsClientConnection>();
        foreach (var kvp in _pathPrefixIndex)
        {
            if (filePath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var clientId in kvp.Value)
                {
                    if (_connections.TryGetValue(clientId, out var conn) &&
                        conn.WebSocket.State == WebSocketState.Open)
                    {
                        result.Add(conn);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 尝试为给定文件路径选择一个最合适的客户端。
    /// 策略：选择路径前缀匹配最长的在线客户端。
    /// </summary>
    public bool TryPickClientForPath(string filePath, out WsClientConnection? client)
    {
        var candidates = GetConnectionsForPath(filePath);
        client = candidates
            .OrderByDescending(c => c.SupportedPaths.Max(p => p.Length))
            .FirstOrDefault();
        return client != null;
    }

    /// <summary>
    /// 获取所有已注册的连接（仅用于管理/调试）。
    /// </summary>
    public ICollection<WsClientConnection> GetAllConnections()
    {
        return _connections.Values;
    }

    /// <summary>
    /// 启动心跳检测定时器，每 30 秒检查一次，
    /// 超过 60 秒无心跳的连接将被自动注销。
    /// </summary>
    public void StartHeartbeatCheck()
    {
        _heartbeatTimer = new Timer(CheckHeartbeat, null, _heartbeatInterval, _heartbeatInterval);
        _logger.LogInformation("Heartbeat check started (interval: {Interval}s)", _heartbeatInterval.TotalSeconds);
    }

    /// <summary>
    /// 停止心跳检测定时器。
    /// </summary>
    public void StopHeartbeatCheck()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// 更新指定客户端的心跳时间戳。
    /// </summary>
    public void UpdateHeartbeat(string clientId)
    {
        if (_connections.TryGetValue(clientId, out var connection))
        {
            connection.LastHeartbeat = DateTime.UtcNow;
        }
    }

    private void CheckHeartbeat(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _connections)
        {
            if ((now - kvp.Value.LastHeartbeat) > _connectionTimeout)
            {
                _logger.LogWarning(
                    "Client {ClientId} heartbeat timeout (last: {LastHeartbeat:O}), marking disconnected",
                    kvp.Key, kvp.Value.LastHeartbeat);
                _ = UnregisterConnectionAsync(kvp.Key);
            }
        }
    }

    /// <summary>
    /// 规范化路径前缀：确保以 / 开头，去掉尾部的 *。
    /// </summary>
    private static string NormalizePrefix(string prefix)
    {
        prefix = prefix.Trim();
        if (!prefix.StartsWith('/'))
        {
            prefix = "/" + prefix;
        }
        // Remove trailing wildcards
        prefix = prefix.TrimEnd('*');
        return prefix;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeatTimer?.Dispose();
        foreach (var kvp in _connections)
        {
            kvp.Value.DisconnectCts.Cancel();
            kvp.Value.WebSocket?.Dispose();
        }
        _connections.Clear();
        _pathPrefixIndex.Clear();
    }
}

/// <summary>
/// 表示一个 WS 客户端连接的内部状态。
/// </summary>
public class WsClientConnection
{
    /// <summary>客户端唯一标识。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>底层 WebSocket 连接。</summary>
    public WebSocket WebSocket { get; set; } = null!;

    /// <summary>连接建立时间。</summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>上次心跳时间。</summary>
    public DateTime LastHeartbeat { get; set; }

    /// <summary>当前客户端存储的总字节数。</summary>
    public long TotalStorageBytes { get; set; }

    /// <summary>支持的路径前缀列表。</summary>
    public List<string> SupportedPaths { get; set; } = new();

    /// <summary>断开连接的 CancellationTokenSource。</summary>
    public CancellationTokenSource DisconnectCts { get; set; } = new();
}
