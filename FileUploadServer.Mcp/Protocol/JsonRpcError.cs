using System.Text.Json.Nodes;

namespace FileUploadServer.Mcp.Protocol;

/// <summary>
/// JSON-RPC 2.0 错误对象。
/// </summary>
public sealed class JsonRpcError
{
    /// <summary>标准 JSON-RPC 错误码。</summary>
    public static class Codes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        /// <summary>请求超时。</summary>
        public const int Timeout = -32000;
        /// <summary>服务不可达（连接拒绝、DNS、SSL）。</summary>
        public const int ServiceUnreachable = -32001;
        /// <summary>未初始化。</summary>
        public const int NotInitialized = -32002;
        /// <summary>权限不足 / 密钥无效。</summary>
        public const int PermissionDenied = -32003;
        /// <summary>限流触发。</summary>
        public const int RateLimited = -32004;
        /// <summary>存储不可用（WS 节点离线）。</summary>
        public const int StorageUnavailable = -32005;
    }

    public int Code { get; init; }
    public string Message { get; init; }
    public JsonNode? Data { get; init; }

    public JsonRpcError(int code, string message, JsonNode? data = null)
    {
        Code = code;
        Message = message;
        Data = data;
    }

    /// <summary>
    /// 序列化为 JSON-RPC error 对象（camelCase，data 为 null 时省略）。
    /// </summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["code"] = Code,
            ["message"] = Message,
        };
        if (Data != null)
        {
            obj["data"] = Data;
        }
        return obj;
    }
}
