using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Middleware;

/// <summary>
/// API Key 鉴权中间件
/// </summary>
public class ApiKeyAuthMiddleware
{
    /// <summary>
    /// 凭据请求头名称（推荐方式）。凭据不再放查询串，避免落入 Nginx 访问日志、浏览器历史与 Referer。
    /// </summary>
    public const string ApiKeyHeaderName = "X-API-Key";

    /// <summary>默认请求体上限：1GB。</summary>
    public const long DefaultMaxRequestBodySize = 1073741824L;

    private readonly RequestDelegate _next;
    private readonly long _maxRequestBodySize;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _maxRequestBodySize = configuration.GetValue<long?>("Upload:MaxRequestBodySize")
            ?? DefaultMaxRequestBodySize;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
    {
        // 跳过admin、public接口和公共文件路径的鉴权
        // - admin接口自己做localhost限制
        // - public接口用于公网申请临时密钥
        // - /p/ 路径由PublicFileMiddleware处理安全和限流
        // - /api/file-public 供 MCP file_set_public 公网调用，控制器自行校验 Admin 密钥
        if (context.Request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/api/public", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/api/file-public", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/p", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // ① 请求体上限闸门：在任何解析之前按 Content-Length 拒绝超限请求。
        //    未携带 Content-Length 的分块传输由 Kestrel MaxRequestBodySize 在读取时兜底。
        var contentLength = context.Request.ContentLength;
        if (contentLength is > 0 && contentLength > _maxRequestBodySize)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsync(
                $"Payload Too Large: request body exceeds {_maxRequestBodySize} bytes");
            return;
        }

        // ② 凭据解析：优先 X-API-Key 请求头，其次兼容 ?key= 查询参数
        //    ⚠️ 绝不读取 Request.Form：读取表单会在鉴权之前触发 multipart 解析，
        //       未鉴权请求即可让服务端缓冲请求体、耗尽内存（CVE-2026-70181）。
        var key = ResolveCredential(context);

        // 检查key是否存在且有效
        if (string.IsNullOrEmpty(key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(
                $"Unauthorized: missing API key (use {ApiKeyHeaderName} header)");
            return;
        }

        var apiKey = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Key == key && !k.IsDeleted);

        if (apiKey == null || !apiKey.IsValid())
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: invalid or expired key");
            return;
        }

        // 将当前API密钥存入HttpContext.Items供后续使用
        context.Items["CurrentApiKey"] = apiKey;

        // 鉴权通过，继续处理
        await _next(context);
    }

    /// <summary>
    /// 解析请求凭据：<c>X-API-Key</c> 请求头优先，<c>?key=</c> 查询参数仅作兼容保留
    /// （浏览器 &lt;a href&gt; 下载链接无法自定义请求头，网页端仍依赖查询参数）。
    /// 不读取请求体，鉴权前的请求体不会被解析。
    /// </summary>
    public static string? ResolveCredential(HttpContext context)
    {
        var header = context.Request.Headers[ApiKeyHeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header))
        {
            return header.Trim();
        }

        var query = context.Request.Query["key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        return null;
    }
}
