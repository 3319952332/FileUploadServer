using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Middleware;

/// <summary>
/// API Key 鉴权中间件
/// </summary>
public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
    {
        // 跳过admin和public接口的鉴权 - admin接口自己做localhost限制
        if (context.Request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/api/public", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 尝试从查询参数获取key
        var key = context.Request.Query["key"].FirstOrDefault();
        
        // 如果查询参数没有，尝试从表单获取
        if (string.IsNullOrEmpty(key) && context.Request.HasFormContentType)
        {
            key = context.Request.Form["key"].FirstOrDefault();
        }

        // 检查key是否存在且有效
        if (string.IsNullOrEmpty(key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: missing key parameter");
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
}
