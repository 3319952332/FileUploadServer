namespace FileUploadServer.Auth.Entities;

/// <summary>
/// 账号在文件服上的密钥（每账号一把，多设备共享）
/// KeyType: User（普通账号，只见自己文件）/ Admin（管理员，见全部文件）
/// </summary>
public class AuthAccountKey
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string FileKey { get; set; } = string.Empty;
    public int FileKeyId { get; set; }
    public string KeyType { get; set; } = "User";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastRenewedAt { get; set; }
}
