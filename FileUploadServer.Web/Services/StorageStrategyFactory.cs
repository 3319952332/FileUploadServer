using FileUploadServer.Core.Interfaces;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 存储策略工厂接口。
/// 按路径模式匹配选择存储策略。
/// 由 StorageStrategyFactory 实现。
/// </summary>
public interface IStorageStrategyFactory
{
    /// <summary>根据路径获取对应的存储策略。</summary>
    IStorageStrategy GetStrategy(string filePath);

    /// <summary>获取默认存储策略。</summary>
    IStorageStrategy GetDefaultStrategy();
}

/// <summary>
/// 存储策略工厂。
/// 按路径模式匹配选择存储策略，支持 Local / WebSocket / Hybrid 三种模式。
/// 路由规则从 storage_config 表（或配置）读取。
/// </summary>
public class StorageStrategyFactory : IStorageStrategyFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StorageStrategyFactory> _logger;

    // 缓存的路由规则列表
    private List<StorageRouteRule> _routes = new();
    private DateTime _routesLastLoaded = DateTime.MinValue;
    private static readonly TimeSpan RouteCacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _routeLock = new();

    /// <summary>默认存储模式。</summary>
    public StorageMode DefaultMode { get; }

    public StorageStrategyFactory(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<StorageStrategyFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;

        DefaultMode = configuration.GetValue<StorageMode?>("Storage:Mode")
                      ?? StorageMode.Local;
    }

    /// <summary>
    /// 根据路径获取对应的存储策略。
    /// 匹配逻辑：按路径前缀从长到短匹配，匹配最长的优先。
    /// 无匹配时返回默认策略。
    /// </summary>
    public IStorageStrategy GetStrategy(string filePath)
    {
        EnsureRoutesLoaded();

        // 按路径前缀长度从长到短排序，取第一个匹配的
        var matchedRoute = _routes
            .Where(r => IsPathMatch(filePath, r.PathPattern))
            .OrderByDescending(r => r.PathPattern.Length)
            .FirstOrDefault();

        if (matchedRoute != null)
        {
            _logger.LogDebug("Route matched: path={Path}, pattern={Pattern}, mode={Mode}",
                filePath, matchedRoute.PathPattern, matchedRoute.Mode);
            return ResolveStrategy(matchedRoute.Mode, matchedRoute.ClientId);
        }

        // 无匹配返回默认
        return ResolveStrategy(DefaultMode, null);
    }

    /// <summary>
    /// 获取默认存储策略。
    /// </summary>
    public IStorageStrategy GetDefaultStrategy()
    {
        return ResolveStrategy(DefaultMode, null);
    }

    /// <summary>
    /// 解析存储策略实例。
    /// </summary>
    private IStorageStrategy ResolveStrategy(StorageMode mode, string? clientId)
    {
        return mode switch
        {
            StorageMode.Local => _serviceProvider.GetRequiredService<LocalStorageStrategy>(),
            StorageMode.WebSocket => _serviceProvider.GetRequiredService<WsStorageStrategy>(),
            StorageMode.Hybrid => ResolveHybridStrategy(clientId),
            _ => _serviceProvider.GetRequiredService<LocalStorageStrategy>()
        };
    }

    /// <summary>
    /// 混合模式：优先 WebSocket，无可用客户端时降级为本地存储。
    /// </summary>
    private IStorageStrategy ResolveHybridStrategy(string? clientId)
    {
        if (!string.IsNullOrEmpty(clientId))
        {
            var wsStrategy = _serviceProvider.GetRequiredService<WsStorageStrategy>();
            return wsStrategy;
        }

        // 尝试找一个在线的 WS 客户端
        var connectionManager = _serviceProvider.GetService<WsConnectionManager>();
        if (connectionManager != null)
        {
            var allConnections = connectionManager.GetAllConnections();
            if (allConnections.Count > 0)
            {
                return _serviceProvider.GetRequiredService<WsStorageStrategy>();
            }
        }

        _logger.LogInformation("No WS client available, falling back to local storage (Hybrid mode)");
        return _serviceProvider.GetRequiredService<LocalStorageStrategy>();
    }

    /// <summary>
    /// 加载路由规则（从配置或数据库）。
    /// </summary>
    private void EnsureRoutesLoaded()
    {
        lock (_routeLock)
        {
            if ((DateTime.UtcNow - _routesLastLoaded) < RouteCacheDuration)
                return;

            try
            {
                var routes = new List<StorageRouteRule>();

                // 从配置加载路由规则
                var configSection = _configuration.GetSection("Storage:Routes");
                if (configSection.Exists())
                {
                    foreach (var child in configSection.GetChildren())
                    {
                        var rule = new StorageRouteRule
                        {
                            PathPattern = child["PathPattern"] ?? "",
                            Mode = Enum.TryParse<StorageMode>(child["Mode"], true, out var mode)
                                ? mode
                                : StorageMode.Local,
                            ClientId = child["ClientId"]
                        };
                        if (!string.IsNullOrEmpty(rule.PathPattern))
                        {
                            routes.Add(rule);
                        }
                    }
                }

                _routes = routes;
                _routesLastLoaded = DateTime.UtcNow;

                _logger.LogInformation("Loaded {Count} storage route rules", _routes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load storage route rules");
                _routesLastLoaded = DateTime.UtcNow; // 避免频繁重试
            }
        }
    }

    /// <summary>
    /// 路径模式匹配。
    /// 支持通配符 *（匹配单段）和 **（匹配多段）。
    /// </summary>
    private static bool IsPathMatch(string filePath, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;

        // 精确前缀匹配（常见场景）
        if (!pattern.Contains('*'))
        {
            return filePath.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        // 通配符匹配（简化版）
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*") + ".*$";
        return System.Text.RegularExpressions.Regex.IsMatch(filePath, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 手动刷新路由缓存。
    /// </summary>
    public void RefreshRoutes()
    {
        lock (_routeLock)
        {
            _routesLastLoaded = DateTime.MinValue;
        }
    }
}

/// <summary>
/// 存储路由规则。
/// </summary>
public class StorageRouteRule
{
    /// <summary>路径模式，如 /public/*, /private/**。</summary>
    public string PathPattern { get; set; } = string.Empty;

    /// <summary>存储模式。</summary>
    public StorageMode Mode { get; set; } = StorageMode.Local;

    /// <summary>关联的 WS 客户端 ID（WebSocket 模式时使用）。</summary>
    public string? ClientId { get; set; }

    /// <summary>优先级（数值越高越优先匹配）。</summary>
    public int Priority { get; set; }
}
