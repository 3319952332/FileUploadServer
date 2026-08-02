using System.Text.Json.Nodes;

namespace FileUploadServer.Mcp.Server;

/// <summary>
/// MCP tools/call 响应（CallToolResult）。text 为 JSON 字符串，isError 标记业务失败。
/// </summary>
public sealed class CallToolResult
{
    public required string Text { get; init; }
    public bool IsError { get; init; }

    public static CallToolResult Success(string text) => new() { Text = text, IsError = false };

    public static CallToolResult Failure(string text) => new() { Text = text, IsError = true };

    public JsonObject ToJson() => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = Text },
        },
        ["isError"] = IsError,
    };
}
