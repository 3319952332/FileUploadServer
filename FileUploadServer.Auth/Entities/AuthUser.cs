namespace FileUploadServer.Auth.Entities;

/// <summary>
/// 登录服务器用户
/// Status: Pending=待管理员审核 / Active=可用 / Disabled=禁用或驳回
/// </summary>
public class AuthUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public bool IsAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
