using System.Text.Json.Nodes;
using FileUploadServer.Mcp.Protocol;

namespace FileUploadServer.Tests.Mcp.TestHelpers;

/// <summary>
/// JsonRpcResponse 的测试断言扩展。
/// </summary>
public static class McpResponseExtensions
{
    /// <summary>tools/call 的 isError 字段。</summary>
    public static bool ToolIsError(this JsonRpcResponse response)
    {
        return response.Result?["isError"]?.GetValue<bool>() ?? false;
    }

    /// <summary>tools/call 的 content[0].text。</summary>
    public static string ToolText(this JsonRpcResponse response)
    {
        var content = response.Result?["content"] as JsonArray;
        return content?[0]?["text"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>将 tools/call 的 text 解析为 JsonObject（成功或错误均为此格式）。</summary>
    public static JsonObject ParseToolText(this JsonRpcResponse response)
    {
        return JsonNode.Parse(response.ToolText()) as JsonObject ?? new JsonObject();
    }

    /// <summary>JSON-RPC error 的 code。</summary>
    public static int? ErrorCode(this JsonRpcResponse response) => response.Error?.Code;

    /// <summary>JSON-RPC error 的 message。</summary>
    public static string? ErrorMessage(this JsonRpcResponse response) => response.Error?.Message;
}
