using System.Text.Json.Nodes;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Server;

namespace FileUploadServer.Mcp.Services;

/// <summary>
/// 将下游 HTTP 状态码映射为 MCP 错误结果（tools/call 的 isError:true 响应）。
/// 错误 text 为 JSON：{"status":"error","error_code":...,"message":...,"data":{...}}，
/// 满足文档"text 中写入明确的错误码和业务语义，不透传 .NET 堆栈"的要求。
/// </summary>
public static class ErrorMapper
{
    /// <summary>
    /// 根据后端响应构造错误 CallToolResult。
    /// </summary>
    /// <param name="response">后端响应（失败状态）</param>
    /// <param name="context">业务上下文，用于更精确的语义（如文件 ID、操作名）</param>
    public static async Task<CallToolResult> ToErrorResultAsync(HttpResponseMessage response, string? context = null)
    {
        var status = (int)response.StatusCode;
        var code = MapCode(status);
        var retryable = status is 429 or 503 or >= 500;

        string body;
        try
        {
            body = (await response.Content.ReadAsStringAsync()).Trim();
        }
        catch
        {
            body = string.Empty;
        }

        var message = BuildMessage(status, body, context);

        var data = new JsonObject
        {
            ["http_status"] = status,
            ["retryable"] = retryable,
        };
        if (status == 429 && response.Headers.RetryAfter?.Delta is { } delta)
        {
            data["retry_after_seconds"] = (int)delta.TotalSeconds;
        }
        else if (status == 429)
        {
            data["retry_after_seconds"] = 30;
        }
        if (context is not null)
        {
            data["context"] = context;
        }

        var text = new JsonObject
        {
            ["status"] = "error",
            ["error_code"] = code,
            ["message"] = message,
            ["data"] = data,
        }.ToJsonString(McpJson.SerializeOptions);

        return CallToolResult.Failure(text);
    }

    private static int MapCode(int httpStatus) => httpStatus switch
    {
        400 or 404 or 413 => JsonRpcError.Codes.InvalidParams, // -32602
        401 or 403 => JsonRpcError.Codes.PermissionDenied,     // -32003
        429 => JsonRpcError.Codes.RateLimited,                 // -32004
        503 => JsonRpcError.Codes.StorageUnavailable,          // -32005
        _ => JsonRpcError.Codes.InternalError,                 // -32603（含 500+）
    };

    private static string BuildMessage(int httpStatus, string body, string? context)
    {
        var hint = context is null ? "" : $"（{context}）";
        var baseMessage = httpStatus switch
        {
            400 => "参数非法",
            404 => "文件不存在",
            413 => "文件过大：超出大小限制",
            401 => "密钥无效或已过期",
            403 => $"权限不足：当前密钥无权访问该资源{hint}",
            429 => "触发限流，请稍后重试",
            503 => "存储节点不可用（WS 节点离线）",
            _ => "服务器内部错误",
        };

        // 优先透传后端返回的业务语义（如 "Storage client is currently offline"）
        if (!string.IsNullOrEmpty(body) && body != baseMessage)
        {
            return $"{baseMessage}：{body}";
        }
        return baseMessage;
    }
}
