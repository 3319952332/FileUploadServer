namespace FileUploadServer.Core.Entities;

/// <summary>
/// 临时API密钥
/// </summary>
public class ApiKey
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 密钥值
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 描述（说明用途）
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 过期时间
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 是否已被手动删除
    /// </summary>
    public bool IsDeleted { get; set; } = false;

    /// <summary>
    /// 检查密钥是否有效
    /// </summary>
    public bool IsValid()
    {
        return !IsDeleted && DateTime.UtcNow < ExpiresAt;
    }
}
