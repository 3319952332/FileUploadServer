using System.Text.Encodings.Web;
using System.Text.Json;

namespace FileUploadServer.Mcp.Protocol;

/// <summary>
/// 共享 JSON 序列化配置：camelCase 命名、大小写不敏感反序列化、
/// 不转义非 ASCII（中文描述可读）。
/// </summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions SerializeOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>将对象序列化为 JSON 文本（camelCase）。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializeOptions);
}
