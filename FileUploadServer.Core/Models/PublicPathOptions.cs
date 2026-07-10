namespace FileUploadServer.Core.Models;

/// <summary>
/// 公共文件访问路径配置
/// </summary>
public class PublicPathOptions
{
    /// <summary>
    /// 公共路径模式列表，支持 * 和 ** 通配符
    /// </summary>
    public string[] Patterns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 公共文件最大大小（字节），默认 50MB
    /// </summary>
    public long MaxFileSize { get; set; } = 52_428_800;

    /// <summary>
    /// 限流配置
    /// </summary>
    public PublicRateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    /// Cache-Control 响应头值，默认 7 天
    /// </summary>
    public string CacheControl { get; set; } = "public,max-age=604800";

    /// <summary>
    /// IP 白名单，非空时仅允许列表中的 IP 访问
    /// </summary>
    public string[] AllowList { get; set; } = Array.Empty<string>();

    /// <summary>
    /// IP 黑名单，列表中的 IP 将被拒绝访问
    /// </summary>
    public string[] DenyList { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 公共文件访问限流配置
/// </summary>
public class PublicRateLimitOptions
{
    /// <summary>
    /// 每 IP 每分钟最大请求数，默认 100
    /// </summary>
    public int PerIpPerMinute { get; set; } = 100;

    /// <summary>
    /// 每文件每分钟最大请求数，默认 20
    /// </summary>
    public int PerFilePerMinute { get; set; } = 20;

    /// <summary>
    /// 最大并发下载数，默认 50
    /// </summary>
    public int ConcurrentDownloads { get; set; } = 50;
}
