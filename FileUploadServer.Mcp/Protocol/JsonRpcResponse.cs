using System.Text.Json.Nodes;

namespace FileUploadServer.Mcp.Protocol;

/// <summary>
/// JSON-RPC 2.0 响应。Result 与 Error 二选一。
/// </summary>
public sealed class JsonRpcResponse
{
    public long? Id { get; init; }
    public JsonNode? Result { get; init; }
    public JsonRpcError? Error { get; init; }

    public static JsonRpcResponse FromResult(long? id, JsonNode? result)
        => new() { Id = id, Result = result };

    public static JsonRpcResponse FromError(long? id, JsonRpcError error)
        => new() { Id = id, Error = error };

    /// <summary>
    /// 序列化为完整 JSON-RPC 响应文本（写入 stdout 前的最后一步）。
    /// </summary>
    public string ToJsonText()
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Id.HasValue ? JsonValue.Create(Id.Value) : null,
        };

        if (Error is not null)
        {
            obj["error"] = Error.ToJson();
        }
        else
        {
            obj["result"] = Result ?? new JsonObject();
        }

        return obj.ToJsonString(McpJson.SerializeOptions);
    }
}
