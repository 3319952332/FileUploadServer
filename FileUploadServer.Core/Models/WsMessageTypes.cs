using System.Text.Json.Serialization;

namespace FileUploadServer.Core.Models;

/// <summary>
/// WebSocket消息基类
/// </summary>
public class WsMessageBase
{
    /// <summary>
    /// 消息类型
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// 请求唯一标识
    /// </summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// Unix时间戳
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>
/// 上传请求消息（网关→客户端）
/// </summary>
public class UploadRequestMessage : WsMessageBase
{
    /// <summary>
    /// 文件路径
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 文件名
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}

/// <summary>
/// 上传确认消息（客户端→网关）
/// </summary>
public class UploadAckMessage : WsMessageBase
{
    /// <summary>
    /// 状态码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; } = 200;

    /// <summary>
    /// 状态消息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "OK";
}

/// <summary>
/// 上传完成消息（客户端→网关）
/// </summary>
public class UploadCompleteMessage : WsMessageBase
{
    /// <summary>
    /// 文件哈希
    /// </summary>
    [JsonPropertyName("fileHash")]
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    /// <summary>
    /// 状态码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; } = 200;

    /// <summary>
    /// 状态消息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "OK";
}

/// <summary>
/// 下载请求消息（网关→客户端）
/// </summary>
public class DownloadRequestMessage : WsMessageBase
{
    /// <summary>
    /// 文件路径
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// 下载数据消息（客户端→网关，控制帧，二进制帧紧随其后）
/// </summary>
public class DownloadDataMessage : WsMessageBase
{
    /// <summary>
    /// 当前分块序号（从0开始）
    /// </summary>
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    /// <summary>
    /// 总分块数
    /// </summary>
    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; }
}

/// <summary>
/// 下载完成消息（客户端→网关）
/// </summary>
public class DownloadCompleteMessage : WsMessageBase
{
    /// <summary>
    /// 状态码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; } = 200;

    /// <summary>
    /// 状态消息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "OK";
}

/// <summary>
/// 删除请求消息（网关→客户端）
/// </summary>
public class DeleteRequestMessage : WsMessageBase
{
    /// <summary>
    /// 文件路径
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// 删除完成消息（客户端→网关）
/// </summary>
public class DeleteCompleteMessage : WsMessageBase
{
    /// <summary>
    /// 状态码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; } = 204;

    /// <summary>
    /// 状态消息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "Deleted";
}

/// <summary>
/// 心跳请求消息
/// </summary>
public class PingMessage : WsMessageBase
{
}

/// <summary>
/// 心跳响应消息
/// </summary>
public class PongMessage : WsMessageBase
{
}

/// <summary>
/// 错误消息
/// </summary>
public class ErrorMessage : WsMessageBase
{
    /// <summary>
    /// 错误码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    /// 错误描述
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
