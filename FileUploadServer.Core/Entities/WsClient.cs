namespace FileUploadServer.Core.Entities;

/// <summary>
/// WebSocket客户端实体
/// </summary>
public class WsClient
{
    /// <summary>
    /// 客户端唯一标识
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 客户端密钥哈希（SHA256）
    /// </summary>
    public string ClientSecretHash { get; set; } = string.Empty;

    /// <summary>
    /// 客户端描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 路径前缀（逗号分隔）
    /// </summary>
    public string PathPrefixes { get; set; } = string.Empty;

    /// <summary>
    /// 存储容量上限（字节），-1表示无限制
    /// </summary>
    public long StorageCapacity { get; set; } = -1;

    /// <summary>
    /// 当前已用存储（字节）
    /// </summary>
    public long CurrentStorage { get; set; }

    /// <summary>
    /// 最后连接时间
    /// </summary>
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
