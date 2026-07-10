namespace FileUploadServer.Core.Entities;

/// <summary>
/// IP白名单实体
/// </summary>
public class IpWhitelist
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// IP地址（支持IPv4和IPv6）
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// 描述/备注
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
