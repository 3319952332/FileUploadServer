using System.Text.Json.Nodes;

namespace FileUploadServer.Mcp.Protocol;

/// <summary>
/// JSON-RPC 2.0 请求或通知。Id 为 null 表示通知（客户端无需响应）。
/// </summary>
public sealed class JsonRpcRequest
{
    public long? Id { get; init; }
    public required string Method { get; init; }
    public JsonNode? Params { get; init; }

    /// <summary>是否为通知（无 id，不需要响应）。</summary>
    public bool IsNotification => Id is null;

    /// <summary>
    /// 从原始 JSON 文本解析请求。解析失败时输出对应的 JSON-RPC 错误（-32700 / -32600）。
    /// </summary>
    public static JsonRpcRequest? TryParse(string json, out JsonRpcError? parseError)
    {
        parseError = null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            parseError = new JsonRpcError(JsonRpcError.Codes.ParseError, "Parse error: invalid JSON");
            return null;
        }

        if (root is not JsonObject obj)
        {
            parseError = new JsonRpcError(JsonRpcError.Codes.InvalidRequest, "Invalid Request: message must be a JSON object");
            return null;
        }

        var jsonrpc = obj["jsonrpc"]?.GetValue<string>();
        if (jsonrpc != "2.0")
        {
            parseError = new JsonRpcError(JsonRpcError.Codes.InvalidRequest, "Invalid Request: jsonrpc must be \"2.0\"");
            return null;
        }

        var method = obj["method"]?.GetValue<string>();
        if (string.IsNullOrEmpty(method))
        {
            parseError = new JsonRpcError(JsonRpcError.Codes.InvalidRequest, "Invalid Request: missing method");
            return null;
        }

        long? id = null;
        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idValue)
        {
            // 仅接受数字或字符串 id；null 视为通知
            if (idValue.TryGetValue<long>(out var longId))
            {
                id = longId;
            }
            else if (idValue.TryGetValue<string>(out var strId) && long.TryParse(strId, out var parsed))
            {
                id = parsed;
            }
        }

        return new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = obj["params"],
        };
    }
}
