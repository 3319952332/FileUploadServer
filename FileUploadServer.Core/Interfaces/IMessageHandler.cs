using System.Text.Json;

namespace FileUploadServer.Core.Interfaces;

/// <summary>
/// WebSocket消息处理器接口
/// </summary>
public interface IMessageHandler
{
    /// <summary>
    /// 处理器对应的消息类型（如 "upload_request"）
    /// </summary>
    string MessageType { get; }

    /// <summary>
    /// 处理消息
    /// </summary>
    /// <param name="clientId">发送消息的客户端ID</param>
    /// <param name="message">JSON消息文档</param>
    /// <param name="payload">二进制载荷（数据帧时不为null）</param>
    /// <param name="connection">连接上下文对象</param>
    Task HandleAsync(string clientId, JsonDocument message, byte[]? payload, object connection);
}
