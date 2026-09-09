namespace FileUploadServer.Auth.Entities;

/// <summary>
/// 账号在文件服上的 User 类型密钥（每账号一把，多设备共享）
/// </summary>
public class AuthAccountKey
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string FileKey { get; set; } = string.Empty;
    public int FileKeyId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastRenewedAt { get; set; }
}
