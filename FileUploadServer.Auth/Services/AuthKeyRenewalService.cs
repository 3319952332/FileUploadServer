using FileUploadServer.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Auth.Services;

/// <summary>
/// 后台保温：定期扫描临近过期的账号密钥并续期，保证活跃账号密钥始终有效。
/// 即使长时间不续期，文件服也不会删除 User 密钥及文件（数据安全兜底）。
/// </summary>
public class AuthKeyRenewalService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthKeyRenewalService> _logger;
    private readonly int _renewThresholdMinutes;
    private readonly int _userKeyExpireMinutes;
    private readonly int _scanMinutes;

    public AuthKeyRenewalService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<AuthKeyRenewalService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _renewThresholdMinutes = config.GetValue("Auth:RenewThresholdMinutes", 43200);
        _userKeyExpireMinutes = config.GetValue("Auth:UserKeyExpireMinutes", 129600);
        _scanMinutes = config.GetValue("Auth:RenewalScanMinutes", 360);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("账号密钥续期服务已启动（每 {ScanMinutes} 分钟扫描，剩余不足 {Threshold} 分钟续期）",
            _scanMinutes, _renewThresholdMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "账号密钥续期扫描失败");
            }
            await Task.Delay(TimeSpan.FromMinutes(_scanMinutes), stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var fileServer = scope.ServiceProvider.GetRequiredService<FileServerAdminClient>();
        var threshold = DateTime.UtcNow.AddMinutes(_renewThresholdMinutes);

        var due = await db.AccountKeys
            .Where(x => x.ExpiresAt < threshold)
            .ToListAsync(ct);

        foreach (var ak in due)
        {
            try
            {
                await fileServer.RenewKeyAsync(ak.FileKey, _userKeyExpireMinutes);
                ak.ExpiresAt = DateTime.UtcNow.AddMinutes(_userKeyExpireMinutes);
                ak.LastRenewedAt = DateTime.UtcNow;
                _logger.LogInformation("后台续期账号密钥 fileKeyId={KeyId} -> {Expires}", ak.FileKeyId, ak.ExpiresAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台续期账号密钥失败 fileKeyId={KeyId}", ak.FileKeyId);
            }
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
