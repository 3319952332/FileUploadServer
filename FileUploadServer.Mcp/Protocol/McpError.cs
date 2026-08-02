using System.Text.Json.Nodes;

namespace FileUploadServer.Mcp.Protocol;

/// <summary>
/// MCP 业务/协议错误。由工具处理器抛出，McpServer 捕获并转换为 JSON-RPC error。
/// </summary>
public sealed class McpError : Exception
{
    public int Code { get; }
    public JsonNode? ErrorData { get; }

    public McpError(int code, string message, JsonNode? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public JsonRpcError ToJsonRpcError() => new(Code, Message, ErrorData);
}
