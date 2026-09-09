namespace FileUploadServer.Auth.Entities;

/// <summary>
/// 登录会话（不透明令牌，库中仅存 SHA-256 哈希，可吊销）
/// </summary>
public class AuthSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
