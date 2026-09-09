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

    /// <summary>确保账号有有效文件密钥；返回 (fileKey, fileKeyExpiresAtUtc)</summary>
    public async Task<(string Key, DateTime ExpiresAt)> EnsureValidAsync(int userId, string username)
    {
        var ak = await _db.AccountKeys.FirstOrDefaultAsync(x => x.UserId == userId);
        if (ak == null)
        {
            var created = await _fileServer.CreateUserKeyAsync(username, _userKeyExpireMinutes);
            ak = new AuthAccountKey
            {
                UserId = userId,
                FileKey = created.Key,
                FileKeyId = created.KeyId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(_userKeyExpireMinutes),
                LastRenewedAt = DateTime.UtcNow,
            };
            _db.AccountKeys.Add(ak);
            await _db.SaveChangesAsync();
            _logger.LogInformation("为账号 {Username}({UserId}) 创建文件密钥 fileKeyId={KeyId}", username, userId, created.KeyId);
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
