using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Core.Models;
using FileUploadServer.Core.Services;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
using FileUploadServer.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileUploadServer.Web.Middleware;

/// <summary>
/// 公共文件访问中间件
/// <para>处理 /p/{*filePath} 路径的匿名文件访问请求，实现完整的 12 步处理流程。</para>
/// </summary>
/// <remarks>
/// ⛔ 2026-08-02 已由 Program.cs 屏蔽（未注册），问题待整改：
/// 1. Step 8.5 WS 存储分支对加密文件服务端解密失败（老密文 tag mismatch → 503）；
/// 2. 中间件直接连 WS 节点违背"访问统一走 API"的分层架构。
/// 整改方向：公开访问统一走 FileApiController.Download 封装，或公开文件限定本地磁盘。
/// </remarks>
public class PublicFileMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PathMatcher _pathMatcher;
    private readonly ILogger<PublicFileMiddleware> _logger;

    /// <summary>
    /// IP 模式缓存的正则生成锁
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> IpPatternCache = new(StringComparer.OrdinalIgnoreCase);

    public PublicFileMiddleware(
        RequestDelegate next,
        PathMatcher pathMatcher,
        ILogger<PublicFileMiddleware> logger)
    {
        _next = next;
        _pathMatcher = pathMatcher;
        _logger = logger;
    }

    /// <summary>
    /// 中间件入口方法，实现完整的公共文件访问处理流程
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext dbContext,
        IPublicFileRateLimiter rateLimiter,
        IWebHostEnvironment env,
        IOptions<PublicPathOptions> options)
    {
        var opts = options.Value;

        // ====================================================================
        // Step 1: 验证路径以 /p/ 开头
        // ====================================================================
        var requestPath = context.Request.Path.Value ?? "";

        if (!requestPath.StartsWith("/p/", StringComparison.OrdinalIgnoreCase))
        {
            // 非公共路径，继续处理下一个中间件
            await _next(context);
            return;
        }

        _logger.LogInformation("公共文件访问请求: {Path}", requestPath);

        // ====================================================================
        // Step 2: 提取文件路径 /p/public/a.jpg -> public/a.jpg
        // ====================================================================
        var filePath = requestPath.AsSpan(3).ToString(); // 移除 "/p/" 前缀

        // ====================================================================
        // Step 3: 路径安全检查
        // ====================================================================
        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning("路径安全检查未通过: {Path}", requestPath);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Bad request: invalid path");
            return;
        }

        // ====================================================================
        // Step 4: PathMatcher 匹配公共路径模式
        // ====================================================================
        if (!_pathMatcher.MatchesAnyPattern(filePath, opts.Patterns))
        {
            _logger.LogWarning("路径未匹配公共模式: {Path}", requestPath);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Not found");
            return;
        }

        // ====================================================================
        // Step 5: IP 白名单/黑名单检查
        // ====================================================================
        var remoteIp = GetRemoteIpAddress(context);

        if (!IsIpAllowed(remoteIp, opts))
        {
            _logger.LogWarning("IP {IP} 被拒绝访问公共路径: {Path}", remoteIp, requestPath);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden: IP not allowed");
            return;
        }

        // ====================================================================
        // Step 6: 限流检查（IP 维度 + 文件维度）
        // ====================================================================
        var (allowed, retryAfterSeconds) = rateLimiter.TryAcquire(remoteIp, filePath);

        if (!allowed)
        {
            _logger.LogWarning("限流触发，拒绝 IP: {IP} 访问: {Path}, 重试等待: {RetryAfter}s",
                remoteIp, requestPath, retryAfterSeconds);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
            await context.Response.WriteAsync($"Too many requests. Retry after {retryAfterSeconds} seconds.");
            return;
        }

        try
        {
            // ====================================================================
            // Step 7: 查找 FileItem（IsPublic == true && PublicPath 匹配）
            // ====================================================================
            var fileItem = await dbContext.Files
                .Where(f => f.IsPublic && f.PublicPath != null && f.PublicPath == filePath)
                .FirstOrDefaultAsync();

            // 老请求中，如果 PublicPath 包含前导斜杠，尝试带斜杠匹配
            fileItem ??= await dbContext.Files
                .Where(f => f.IsPublic && f.PublicPath != null && f.PublicPath == "/" + filePath)
                .FirstOrDefaultAsync();

            if (fileItem == null)
            {
                _logger.LogWarning("公共文件记录不存在: {Path}", filePath);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("File not found");
                return;
            }

            // ====================================================================
            // Step 8: 检查文件大小限制
            // ====================================================================
            if (fileItem.FileSize > opts.MaxFileSize)
            {
                _logger.LogWarning("文件大小超过限制: {Path} ({Size} > {MaxSize})",
                    filePath, fileItem.FileSize, opts.MaxFileSize);
                context.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
                await context.Response.WriteAsync("File too large for public access");
                return;
            }

            // ====================================================================
            // Step 8.5: WebSocket 存储模式 - 从远程 WS 客户端读取文件
            // ====================================================================
            if (fileItem.StorageMode == "WebSocket" && !string.IsNullOrEmpty(fileItem.ClientId))
            {
                var strategy = context.RequestServices.GetRequiredService<WsStorageStrategy>();
                var connectionManager = context.RequestServices.GetRequiredService<WsConnectionManager>();

                var storagePath = fileItem.StoragePath ?? fileItem.PublicPath ?? filePath;

                // 检查 WS 客户端是否在线
                if (!connectionManager.TryPickClientForPath(storagePath, out _))
                {
                    _logger.LogWarning("WS 存储节点不可用: ClientId={ClientId}, Path={Path}",
                        fileItem.ClientId, filePath);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("Storage node temporarily unavailable");
                    return;
                }

                Stream? wsStream = null;
                try
                {
                    _logger.LogInformation("从 WS 存储节点读取公共文件: {Path} (ClientId: {ClientId}, StoragePath: {StoragePath})",
                        filePath, fileItem.ClientId, storagePath);

                    wsStream = await strategy.ReadAsync(storagePath);

                    // 缓冲 WS 流，如需要则解密
                    Stream finalStream = wsStream;
                    if (fileItem.EncryptionVersion > 0)
                    {
                        try
                        {
                            var keyProvider = context.RequestServices.GetService<IKeyProvider>();
                            if (keyProvider != null)
                                finalStream = new AesGcmDecryptStream(wsStream, keyProvider);
                        }
                        catch { /* 解密不可用时返回原始流 */ }
                    }

                    using var wsMs = new MemoryStream();
                    await finalStream.CopyToAsync(wsMs);
                    var wsData = wsMs.ToArray();

                    // 设置响应头
                    var wsContentType = string.IsNullOrEmpty(fileItem.ContentType)
                        ? "application/octet-stream"
                        : fileItem.ContentType;

                    context.Response.ContentType = wsContentType;

                    var wsContentDisposition = IsPreviewableContentType(wsContentType)
                        ? $"inline; filename=\"{fileItem.FileName}\""
                        : $"attachment; filename=\"{fileItem.FileName}\"";

                    context.Response.Headers["Content-Disposition"] = wsContentDisposition;
                    context.Response.Headers["Cache-Control"] = opts.CacheControl;

                    var wsEtag = GenerateEtag(fileItem.StoredFileName, fileItem.FileSize, fileItem.UploadedAt);
                    context.Response.Headers["ETag"] = wsEtag;
                    context.Response.Headers["Last-Modified"] = fileItem.UploadedAt.ToString("R");
                    context.Response.ContentLength = wsData.Length;
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";

                    // 检查条件请求（If-None-Match -> 304 Not Modified）
                    var wsIfNoneMatch = context.Request.Headers["If-None-Match"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(wsIfNoneMatch) && wsIfNoneMatch == wsEtag)
                    {
                        _logger.LogDebug("ETag 匹配，返回 304: {Path} ETag: {Etag}", filePath, wsEtag);
                        context.Response.StatusCode = StatusCodes.Status304NotModified;
                        context.Response.ContentLength = null;
                        context.Response.Headers.Remove("Content-Type");
                        context.Response.Headers.Remove("Content-Disposition");
                        return;
                    }

                    _logger.LogInformation("开始流式返回公共文件 (WS): {Path} (ID: {Id}, Size: {Size})",
                        filePath, fileItem.Id, wsData.Length);

                    await context.Response.Body.WriteAsync(wsData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "从 WS 存储节点读取文件失败: {Path} (ClientId: {ClientId})",
                        filePath, fileItem.ClientId);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("Storage node temporarily unavailable");
                }
                finally
                {
                    if (wsStream != null)
                        await wsStream.DisposeAsync();
                }

                return;
            }

            // ====================================================================
            // Step 9: 打开文件流（本地磁盘存储模式）
            // ====================================================================
            var uploadsPath = Path.Combine(env.WebRootPath, "uploads");
            var physicalPath = Path.Combine(uploadsPath, fileItem.StoredFileName);

            if (!System.IO.File.Exists(physicalPath))
            {
                _logger.LogError("文件在磁盘上不存在: {PhysicalPath} (FileItem ID: {Id}, StoredFileName: {Stored})",
                    physicalPath, fileItem.Id, fileItem.StoredFileName);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("File not found");
                return;
            }

            // ====================================================================
            // Step 10: 设置响应头
            // ====================================================================
            var contentType = string.IsNullOrEmpty(fileItem.ContentType)
                ? "application/octet-stream"
                : fileItem.ContentType;

            context.Response.ContentType = contentType;

            // Content-Disposition: inline 允许浏览器预览
            var contentDisposition = IsPreviewableContentType(contentType)
                ? $"inline; filename=\"{fileItem.FileName}\""
                : $"attachment; filename=\"{fileItem.FileName}\"";

            context.Response.Headers["Content-Disposition"] = contentDisposition;

            // Cache-Control
            context.Response.Headers["Cache-Control"] = opts.CacheControl;

            // ETag（使用文件存储名、大小、上传时间的组合）
            var etag = GenerateEtag(fileItem.StoredFileName, fileItem.FileSize, fileItem.UploadedAt);
            context.Response.Headers["ETag"] = etag;

            // Last-Modified
            context.Response.Headers["Last-Modified"] = fileItem.UploadedAt.ToString("R");

            // Content-Length
            context.Response.ContentLength = fileItem.FileSize;

            // 额外的安全头
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";

            // ====================================================================
            // Step 11: 检查条件请求（If-None-Match -> 304 Not Modified）
            // ====================================================================
            var ifNoneMatch = context.Request.Headers["If-None-Match"].FirstOrDefault();
            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
            {
                _logger.LogDebug("ETag 匹配，返回 304: {Path} ETag: {Etag}", filePath, etag);
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.ContentLength = null;
                context.Response.Headers.Remove("Content-Type");
                context.Response.Headers.Remove("Content-Disposition");
                return;
            }

            // ====================================================================
            // Step 12: 流式返回文件内容
            // ====================================================================
            _logger.LogInformation("开始流式返回公共文件: {Path} (ID: {Id}, Size: {Size})",
                filePath, fileItem.Id, fileItem.FileSize);

            await using (var fileStream = new FileStream(
                             physicalPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 65536, // 64KB 缓冲区
                             useAsync: true))
            {
                // 当 Phase 1.5 加密功能实现后，在此处应检查 fileItem.EncryptionVersion > 0，
                // 如果加密已启用，将 fileStream 包装为 AesGcmDecryptStream 进行透明解密：
                //
                // if (fileItem.EncryptionVersion > 0)
                // {
                //     var keyProvider = context.RequestServices.GetRequiredService<IKeyProvider>();
                //     var decryptStream = new AesGcmDecryptStream(fileStream, keyProvider, fileItem.KeyVersion, fileItem.BlockSize);
                //     await decryptStream.CopyToAsync(context.Response.Body);
                // }
                // else
                // {
                await fileStream.CopyToAsync(context.Response.Body);
                // }
            }
        }
        finally
        {
            // 无论成功或异常，都释放限流槽位
            rateLimiter.Release();
        }
    }

    /// <summary>
    /// 路径安全检查
    /// </summary>
    private static bool IsPathSafe(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // 拒绝空路径和根路径
        if (path.Length == 0 || path == "/")
            return false;

        // 拒绝包含路径遍历特征的路径
        if (path.Contains("..", StringComparison.Ordinal))
        {
            // 排除合法路径如 /.../ （省略号）的情况
            if (!path.Contains("/...", StringComparison.Ordinal) &&
                !path.Contains("\\...", StringComparison.Ordinal))
            {
                return false;
            }
        }

        // 拒绝包含空字节等危险字符
        if (path.Contains('\0'))
            return false;

        // 路径最大长度限制
        if (path.Length > 2048)
            return false;

        // 拒绝以斜杠开头的路径（防止绝对路径引用）
        if (path.StartsWith('/') || path.StartsWith('\\'))
            return false;

        return true;
    }

    /// <summary>
    /// 获取客户端真实 IP 地址
    /// </summary>
    private static string GetRemoteIpAddress(HttpContext context)
    {
        // 优先使用 X-Forwarded-For 头（反向代理场景）
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // X-Forwarded-For 可能包含逗号分隔的多个 IP，取第一个（客户端原始 IP）
            var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ips.Length > 0 && !string.IsNullOrEmpty(ips[0]))
                return ips[0];
        }

        // 回退到直接连接 IP
        var remoteIp = context.Connection.RemoteIpAddress;
        return remoteIp?.ToString() ?? "unknown";
    }

    /// <summary>
    /// IP 白名单/黑名单检查
    /// </summary>
    private static bool IsIpAllowed(string ipAddress, PublicPathOptions opts)
    {
        // 检查黑名单（DenyList 优先）
        if (opts.DenyList.Length > 0)
        {
            foreach (var denyPattern in opts.DenyList)
            {
                if (IsIpMatch(ipAddress, denyPattern))
                    return false;
            }
        }

        // 检查白名单（AllowList 非空时，仅允许列表中的 IP）
        if (opts.AllowList.Length > 0)
        {
            foreach (var allowPattern in opts.AllowList)
            {
                if (IsIpMatch(ipAddress, allowPattern))
                    return true;
            }
            // 白名单非空但 IP 不在其中 -> 拒绝
            return false;
        }

        // 无白名单配置，放行（除非被黑名单拦截）
        return true;
    }

    /// <summary>
    /// IP 地址匹配（支持 * 通配符，如 192.168.* 匹配 192.168.0.1）
    /// </summary>
    private static bool IsIpMatch(string ipAddress, string pattern)
    {
        if (string.IsNullOrEmpty(ipAddress) || string.IsNullOrEmpty(pattern))
            return false;

        if (pattern.Contains('*'))
        {
            var regex = IpPatternCache.GetOrAdd(pattern, p =>
            {
                var escaped = Regex.Escape(p).Replace("\\*", ".*");
                return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            });

            return regex.IsMatch(ipAddress);
        }

        return string.Equals(ipAddress, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断 Content-Type 是否可浏览器预览
    /// </summary>
    private static bool IsPreviewableContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        var previewablePrefixes = new[]
        {
            "text/", "image/", "audio/", "video/",
            "application/pdf", "application/json", "application/xml"
        };

        foreach (var prefix in previewablePrefixes)
        {
            if (contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 生成 ETag 值
    /// </summary>
    private static string GenerateEtag(string storedFileName, long fileSize, DateTime uploadTime)
    {
        // 使用文件元数据生成唯一的 ETag
        var input = $"{storedFileName}-{fileSize}-{uploadTime.Ticks:X16}";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hexHash = Convert.ToHexStringLower(hash);
        return $"\"{hexHash[..16]}\""; // 取前 16 位作为 ETag
    }
}
