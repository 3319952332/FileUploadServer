using System.Collections.Concurrent;
using FileUploadServer.Core.Models;
using Microsoft.Extensions.Options;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 公共文件访问限流器接口
/// </summary>
public interface IPublicFileRateLimiter
{
    /// <summary>
    /// 尝试获取访问配额
    /// </summary>
    /// <param name="ipAddress">客户端 IP 地址</param>
    /// <param name="filePath">请求的文件路径</param>
    /// <returns>元组：(是否允许访问, 建议的重试等待秒数)</returns>
    (bool Allowed, int RetryAfterSeconds) TryAcquire(string ipAddress, string filePath);

    /// <summary>
    /// 释放一个并发下载槽位
    /// </summary>
    void Release();
}

/// <summary>
/// 公共文件访问限流器
/// <para>使用 ConcurrentDictionary 按 IP 和文件路径分别限流，使用 SemaphoreSlim 控制并发下载数</para>
/// </summary>
public class PublicFileRateLimiter : IPublicFileRateLimiter
{
    private readonly ConcurrentDictionary<string, RateBucket> _ipBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RateBucket> _fileBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrentSemaphore;
    private readonly PublicRateLimitOptions _options;
    private readonly ILogger<PublicFileRateLimiter> _logger;
    private readonly TimeSpan _bucketCleanupInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastCleanup = DateTime.UtcNow;

    public PublicFileRateLimiter(IOptions<PublicPathOptions> options, ILogger<PublicFileRateLimiter> logger)
    {
        _options = options.Value.RateLimit;
        _concurrentSemaphore = new SemaphoreSlim(_options.ConcurrentDownloads, _options.ConcurrentDownloads);
        _logger = logger;
    }

    /// <inheritdoc />
    public (bool Allowed, int RetryAfterSeconds) TryAcquire(string ipAddress, string filePath)
    {
        // 1. 检查并发下载数
        if (!_concurrentSemaphore.Wait(0))
        {
            _logger.LogWarning("并发下载数已达上限 ({MaxConcurrent})，拒绝请求 IP: {IP} 文件: {Path}",
                _options.ConcurrentDownloads, ipAddress, filePath);
            return (false, CalculateRetryAfter(_options.ConcurrentDownloads, DateTime.UtcNow));
        }

        try
        {
            // 2. 检查 IP 维度限流
            var ipBucket = _ipBuckets.GetOrAdd(ipAddress, _ => new RateBucket(_options.PerIpPerMinute, TimeSpan.FromMinutes(1)));
            if (!ipBucket.TryConsume())
            {
                _logger.LogWarning("IP {IP} 请求过于频繁（每分钟上限 {MaxPerIp}），拒绝请求", ipAddress, _options.PerIpPerMinute);
                return (false, 60);
            }

            // 3. 检查文件维度限流
            var fileBucket = _fileBuckets.GetOrAdd(filePath, _ => new RateBucket(_options.PerFilePerMinute, TimeSpan.FromMinutes(1)));
            if (!fileBucket.TryConsume())
            {
                _logger.LogWarning("文件 {Path} 被请求过于频繁（每分钟上限 {MaxPerFile}），拒绝 IP: {IP}",
                    filePath, _options.PerFilePerMinute, ipAddress);
                return (false, 60);
            }

            return (true, 0);
        }
        catch
        {
            // 如果限流检查过程中出现异常，释放已获取的信号量
            _concurrentSemaphore.Release();
            throw;
        }

        // 注意：正常获取配额后调用方需在完成后调用 Release() 释放信号量
    }

    /// <inheritdoc />
    public void Release()
    {
        _concurrentSemaphore.Release();
    }

    /// <summary>
    /// 定期清理过期桶数据，防止内存泄漏
    /// </summary>
    public void CleanupExpiredBuckets()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCleanup < _bucketCleanupInterval)
            return;

        _lastCleanup = now;

        var removedIpBuckets = 0;
        foreach (var (key, bucket) in _ipBuckets)
        {
            if (bucket.IsExpired())
            {
                _ipBuckets.TryRemove(key, out _);
                removedIpBuckets++;
            }
        }

        var removedFileBuckets = 0;
        foreach (var (key, bucket) in _fileBuckets)
        {
            if (bucket.IsExpired())
            {
                _fileBuckets.TryRemove(key, out _);
                removedFileBuckets++;
            }
        }

        if (removedIpBuckets > 0 || removedFileBuckets > 0)
        {
            _logger.LogDebug("已清理过期限流桶：{IpBucketCount} 个 IP 桶，{FileBucketCount} 个文件桶",
                removedIpBuckets, removedFileBuckets);
        }
    }

    /// <summary>
    /// 计算重试等待秒数
    /// </summary>
    private static int CalculateRetryAfter(int maxConcurrent, DateTime now)
    {
        // 并发满时估算等待时间（基于平均处理时间的保守估算）
        return 5;
    }

    /// <summary>
    /// 滑动窗口限流桶
    /// </summary>
    private sealed class RateBucket
    {
        private readonly Queue<DateTime> _timestamps = new();
        private readonly int _limit;
        private readonly TimeSpan _window;
        private readonly object _lock = new();

        /// <summary>
        /// 最近一次消费的时间戳（用于过期判断）
        /// </summary>
        private DateTime _lastConsumedAt = DateTime.MinValue;

        public RateBucket(int limit, TimeSpan window)
        {
            _limit = limit;
            _window = window;
        }

        /// <summary>
        /// 尝试消费一次配额
        /// </summary>
        /// <returns>是否允许消费</returns>
        public bool TryConsume()
        {
            var now = DateTime.UtcNow;
            var cutoff = now - _window;

            lock (_lock)
            {
                // 移除窗口外的过期时间戳
                while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                {
                    _timestamps.Dequeue();
                }

                if (_timestamps.Count >= _limit)
                    return false;

                _timestamps.Enqueue(now);
                _lastConsumedAt = now;
                return true;
            }
        }

        /// <summary>
        /// 判断桶是否已过期（超过窗口时间无活动）
        /// </summary>
        public bool IsExpired()
        {
            lock (_lock)
            {
                return _timestamps.Count == 0 ||
                       DateTime.UtcNow - _lastConsumedAt > _window * 2;
            }
        }
    }
}
