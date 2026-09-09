using FileUploadServer.Auth.Data;
using FileUploadServer.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Auth.Services;

/// <summary>
/// 账号密钥管理：首次登录创建，过期/临近过期时激活续期。
/// 过期时间由本服务按"请求的分钟数 + UtcNow"自行跟踪，不依赖文件服返回值的时区。
/// </summary>
public class AccountKeyService
{
    private readonly AuthDbContext _db;
    private readonly FileServerAdminClient _fileServer;
    private readonly ILogger<AccountKeyService> _logger;
    private readonly int _userKeyExpireMinutes;
    private readonly int _renewThresholdMinutes;

    public AccountKeyService(
        AuthDbContext db,
        FileServerAdminClient fileServer,
        IConfiguration config,
        ILogger<AccountKeyService> logger)
    {
        _db = db;
        _fileServer = fileServer;
        _logger = logger;
        _userKeyExpireMinutes = config.GetValue("Auth:UserKeyExpireMinutes", 129600);
        _renewThresholdMinutes = config.GetValue("Auth:RenewThresholdMinutes", 43200);
    }

    /// <summary>
    /// 确保账号有有效文件密钥；返回 (fileKey, fileKeyExpiresAtUtc)
    /// 普通账号 → User 密钥（只见自己文件）；管理员账号 → Admin 密钥（文件服原生：见全部文件）
    /// </summary>
    public async Task<(string Key, DateTime ExpiresAt)> EnsureValidAsync(int userId, string username, bool isAdmin)
    {
        var desiredType = isAdmin ? "Admin" : "User";
        var ak = await _db.AccountKeys.FirstOrDefaultAsync(x => x.UserId == userId);

        // 类型不匹配（如普通用户升级为管理员，或历史 User 密钥）：回收旧密钥并重建
        if (ak != null && ak.KeyType != desiredType)
        {
            try
            {
                await _fileServer.DeleteKeyAsync(ak.FileKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "回收旧密钥失败 fileKeyId={KeyId}", ak.FileKeyId);
            }
            _db.AccountKeys.Remove(ak);
            await _db.SaveChangesAsync();
            _logger.LogInformation("账号 {Username}({UserId}) 密钥类型由 {Old} 调整为 {New}，已回收重建", username, userId, ak.KeyType, desiredType);
            ak = null;
        }

        if (ak == null)
        {
            var created = await _fileServer.CreateUserKeyAsync(username, desiredType, _userKeyExpireMinutes);
            ak = new AuthAccountKey
            {
                UserId = userId,
                FileKey = created.Key,
                FileKeyId = created.KeyId,
                KeyType = desiredType,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(_userKeyExpireMinutes),
                LastRenewedAt = DateTime.UtcNow,
            };
            _db.AccountKeys.Add(ak);
            await _db.SaveChangesAsync();
            _logger.LogInformation("为账号 {Username}({UserId}) 创建 {Type} 文件密钥 fileKeyId={KeyId}", username, userId, desiredType, created.KeyId);
            return (ak.FileKey, ak.ExpiresAt);
        }

        var remaining = ak.ExpiresAt - DateTime.UtcNow;
        if (remaining < TimeSpan.FromMinutes(_renewThresholdMinutes))
        {
            await _fileServer.RenewKeyAsync(ak.FileKey, _userKeyExpireMinutes);
            ak.ExpiresAt = DateTime.UtcNow.AddMinutes(_userKeyExpireMinutes);
            ak.LastRenewedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogInformation("登录激活/续期账号 {Username}({UserId}) 文件密钥 fileKeyId={KeyId}", username, userId, ak.FileKeyId);
        }

        return (ak.FileKey, ak.ExpiresAt);
    }
}
