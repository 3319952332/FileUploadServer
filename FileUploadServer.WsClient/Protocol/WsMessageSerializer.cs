using System.Text;
using System.Text.Json;
using FileUploadServer.Core.Models;

namespace FileUploadServer.WsClient.Protocol;

/// <summary>
/// WebSocket 消息序列化/反序列化工具
/// </summary>
public static class WsMessageSerializer
{
    /// <summary>
    /// JSON 序列化配置：camelCase naming policy
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 反序列化配置（不区分大小写）
    /// </summary>
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 将消息序列化为 JSON 字符串
    /// </summary>
    public static string Serialize<T>(T message)
    {
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    /// <summary>
    /// 将 JSON 字符串反序列化为指定类型
    /// </summary>
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, DeserializeOptions);
    }

    /// <summary>
    /// 将 JSON 字符串解析为通用消息对象，返回 (Type, RequestId, JsonDocument)
    /// </summary>
    public static (string type, string requestId, JsonDocument doc) Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString() ?? string.Empty;
        var requestId = doc.RootElement.TryGetProperty("requestId", out var ridProp)
            ? ridProp.GetString() ?? string.Empty
            : string.Empty;
        return (type, requestId, doc);
    }

    /// <summary>
    /// 根据 type 字段将 JSON 反序列化为对应的强类型消息
    /// </summary>
    public static object? DeserializeByType(string json)
    {
        var (type, _, doc) = Parse(json);

        object? result = type switch
        {
            "upload_request" => JsonSerializer.Deserialize<UploadRequestMessage>(json, DeserializeOptions),
            "upload_ack" => JsonSerializer.Deserialize<UploadAckMessage>(json, DeserializeOptions),
            "upload_complete" => JsonSerializer.Deserialize<UploadCompleteMessage>(json, DeserializeOptions),
            "download_request" => JsonSerializer.Deserialize<DownloadRequestMessage>(json, DeserializeOptions),
            "download_data" => JsonSerializer.Deserialize<DownloadDataMessage>(json, DeserializeOptions),
            "download_complete" => JsonSerializer.Deserialize<DownloadCompleteMessage>(json, DeserializeOptions),
            "delete_request" => JsonSerializer.Deserialize<DeleteRequestMessage>(json, DeserializeOptions),
            "delete_complete" => JsonSerializer.Deserialize<DeleteCompleteMessage>(json, DeserializeOptions),
            "ping" => JsonSerializer.Deserialize<PingMessage>(json, DeserializeOptions),
            "pong" => JsonSerializer.Deserialize<PongMessage>(json, DeserializeOptions),
            "error" => JsonSerializer.Deserialize<ErrorMessage>(json, DeserializeOptions),
            _ => null,
        };

        doc.Dispose();
        return result;
    }

    /// <summary>
    /// 构建二进制帧
    /// </summary>
    public static byte[] BuildBinaryFrame(Guid requestId, int chunkIndex, int totalChunks, byte[] data, int dataLength)
    {
        var frame = new byte[WsBinaryFrame.HeaderSize + dataLength];

        // requestId: 16 bytes (GUID binary)
        requestId.TryWriteBytes(frame.AsSpan(0, 16));

        // chunkIndex: 4 bytes (big-endian)
        frame[16] = (byte)((uint)chunkIndex >> 24);
        frame[17] = (byte)((uint)chunkIndex >> 16);
        frame[18] = (byte)((uint)chunkIndex >> 8);
        frame[19] = (byte)chunkIndex;

        // totalChunks: 4 bytes (big-endian)
        frame[20] = (byte)((uint)totalChunks >> 24);
        frame[21] = (byte)((uint)totalChunks >> 16);
        frame[22] = (byte)((uint)totalChunks >> 8);
        frame[23] = (byte)totalChunks;

        // payload data
        Array.Copy(data, 0, frame, WsBinaryFrame.HeaderSize, dataLength);

        return frame;
    }

    /// <summary>
    /// 解析二进制帧，返回 (requestId, chunkIndex, totalChunks, payload)
    /// </summary>
    public static (Guid requestId, int chunkIndex, int totalChunks, byte[] payload) ParseBinaryFrame(byte[] frame)
    {
        var requestIdBytes = new byte[16];
        Array.Copy(frame, 0, requestIdBytes, 0, 16);
        var requestId = new Guid(requestIdBytes);

        var chunkIndex = (frame[16] << 24) | (frame[17] << 16) | (frame[18] << 8) | frame[19];
        var totalChunks = (frame[20] << 24) | (frame[21] << 16) | (frame[22] << 8) | frame[23];

        var payloadLength = frame.Length - WsBinaryFrame.HeaderSize;
        var payload = new byte[payloadLength];
        Array.Copy(frame, WsBinaryFrame.HeaderSize, payload, 0, payloadLength);

        return (requestId, chunkIndex, totalChunks, payload);
    }
}
