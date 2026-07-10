using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 路由策略枚举。
/// </summary>
public enum RouteStrategy
{
    /// <summary>按路径前缀路由（最常用）。</summary>
    PathPrefix,

    /// <summary>同一前缀下轮询。</summary>
    RoundRobin,

    /// <summary>选择存储用量最低的。</summary>
    LeastStorage,

    /// <summary>按容量加权随机。</summary>
    WeightedRandom
}

/// <summary>
/// 多客户端路由器。
/// 支持四种路由策略：PathPrefix, RoundRobin, LeastStorage, WeightedRandom。
/// 包含故障转移机制和健康度评分。
/// </summary>
public class ClientRouter
{
    private readonly WsConnectionManager _connectionManager;
    private readonly ILogger<ClientRouter> _logger;

    /// <summary>轮询计数器（按策略实例）。</summary>
    private long _roundRobinCounter;

    /// <summary>不可用客户端冷却时间（秒）。</summary>
    private const int CooldownSeconds = 30;

    /// <summary>不可用客户端记录（clientId → 冷却到期时间）。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _cooldownClients = new();

    /// <summary>当前路由策略。</summary>
    public RouteStrategy Strategy { get; set; } = RouteStrategy.PathPrefix;

    public ClientRouter(
        WsConnectionManager connectionManager,
        ILogger<ClientRouter> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// 为指定文件路径选择最合适的客户端连接。
    /// 1. 路径前缀匹配 → 过滤不可用 → 按策略选择
    /// 2. 支持故障转移
    /// </summary>
    public WsClientConnection? SelectClient(string filePath)
    {
        // 1. 获取所有匹配路径的在线客户端
        var candidates = _connectionManager.GetConnectionsForPath(filePath);

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No online clients available for path: {Path}", filePath);
            return null;
        }

        // 2. 过滤冷却中的客户端
        var available = candidates
            .Where(c => !IsInCooldown(c.ClientId))
            .ToList();

        if (available.Count == 0)
        {
            // 所有候选都在冷却中，等待第一个恢复
            var earliestCooldown = candidates
                .Select(c => _cooldownClients.TryGetValue(c.ClientId, out var t) ? t : DateTime.MinValue)
                .Min();
            _logger.LogWarning(
                "All clients for path {Path} are in cooldown until {EarliestRelease}, selecting best anyway",
                filePath, earliestCooldown);

            // 降级：从冷却中选择健康度最高的
            available = candidates;
        }

        // 3. 按策略选择
        return Strategy switch
        {
            RouteStrategy.PathPrefix => SelectByPathPrefix(filePath, available),
            RouteStrategy.RoundRobin => SelectByRoundRobin(available),
            RouteStrategy.LeastStorage => SelectByLeastStorage(available),
            RouteStrategy.WeightedRandom => SelectByWeightedRandom(available),
            _ => SelectByPathPrefix(filePath, available)
        };
    }

    /// <summary>
    /// 路径前缀策略：选择路径前缀匹配最长的在线客户端。
    /// </summary>
    private static WsClientConnection? SelectByPathPrefix(string filePath, List<WsClientConnection> candidates)
    {
        return candidates
            .OrderByDescending(c => c.SupportedPaths
                .Where(p => filePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                .DefaultIfEmpty("")
                .Max(p => p.Length))
            .ThenByDescending(c => CalculateHealthScore(c))
            .FirstOrDefault();
    }

    /// <summary>
    /// 轮询策略：轮流选择同前缀下的客户端。
    /// </summary>
    private WsClientConnection? SelectByRoundRobin(List<WsClientConnection> candidates)
    {
        if (candidates.Count == 0) return null;
        var index = (int)(Interlocked.Increment(ref _roundRobinCounter) % candidates.Count);
        return candidates[Math.Abs(index % candidates.Count)];
    }

    /// <summary>
    /// 最少存储策略：选择 CurrentStorage 最小的客户端。
    /// </summary>
    private static WsClientConnection? SelectByLeastStorage(List<WsClientConnection> candidates)
    {
        return candidates.OrderBy(c => c.TotalStorageBytes).FirstOrDefault();
    }

    /// <summary>
    /// 加权随机策略：按容量加权随机选择。
    /// 容量越大（StorageCapacity），被选中的概率越高。
    /// </summary>
    private static WsClientConnection? SelectByWeightedRandom(List<WsClientConnection> candidates)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // 计算权重
        var totalWeight = candidates.Sum(c => Math.Max(c.TotalStorageBytes, 1));
        var random = Random.Shared.NextDouble() * totalWeight;

        double cumulative = 0;
        foreach (var candidate in candidates)
        {
            cumulative += Math.Max(candidate.TotalStorageBytes, 1);
            if (random <= cumulative)
                return candidate;
        }

        return candidates[^1];
    }

    /// <summary>
    /// 标记客户端为暂时不可用，进入冷却期。
    /// </summary>
    public void MarkUnavailable(string clientId)
    {
        var cooldownUntil = DateTime.UtcNow.AddSeconds(CooldownSeconds);
        _cooldownClients[clientId] = cooldownUntil;
        _logger.LogWarning("Client {ClientId} marked unavailable until {CooldownUntil}", clientId, cooldownUntil);
    }

    /// <summary>
    /// 检查客户端是否在冷却中。
    /// </summary>
    public bool IsInCooldown(string clientId)
    {
        if (_cooldownClients.TryGetValue(clientId, out var cooldownUntil))
        {
            if (DateTime.UtcNow < cooldownUntil)
            {
                return true;
            }
            // 冷却已过，移除记录
            _cooldownClients.TryRemove(clientId, out _);
        }
        return false;
    }

    /// <summary>
    /// 获取所有冷却中的客户端。
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> GetCooldownClients()
    {
        // 清理过期的冷却记录
        var now = DateTime.UtcNow;
        foreach (var kvp in _cooldownClients)
        {
            if (now >= kvp.Value)
            {
                _cooldownClients.TryRemove(kvp.Key, out _);
            }
        }
        return _cooldownClients;
    }

    /// <summary>
    /// 清除所有冷却标记。
    /// </summary>
    public void ClearCooldowns()
    {
        _cooldownClients.Clear();
        _logger.LogInformation("All client cooldowns cleared");
    }

    /// <summary>
    /// 计算客户端健康度评分。
    /// 基础分 100，按心跳延迟、存储使用率和启用状态扣分。
    /// </summary>
    public static double CalculateHealthScore(WsClientConnection connection)
    {
        var score = 100.0;

        // 心跳延迟扣分（每秒扣 2 分）
        var secondsSinceHeartbeat = (DateTime.UtcNow - connection.LastHeartbeat).TotalSeconds;
        score -= secondsSinceHeartbeat * 2;

        // 存储使用率扣分（每 1% 扣 0.3 分）
        // 注意：这里没有客户端的 StorageCapacity 信息，使用 TotalStorageBytes 作为粗略指标
        // 实际应用中应从 WsClient 实体获取 StorageCapacity

        // 连接状态扣分
        if (connection.WebSocket.State != WebSocketState.Open)
        {
            score -= 50;
        }

        return Math.Max(0, score);
    }
}
