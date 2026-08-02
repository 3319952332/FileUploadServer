using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 后台清理服务 - 定期清理过期的临时密钥及其关联文件
/// </summary>
public class BackgroundCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<BackgroundCleanupService> _logger;

    public BackgroundCleanupService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<BackgroundCleanupService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("后台清理服务已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredKeysAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理过期密钥时发生错误");
            }

            // 每小时执行一次
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task CleanupExpiredKeysAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("开始清理过期的临时密钥...");

        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        // 查找所有已过期的Temporary类型密钥
        var expiredKeys = await dbContext.ApiKeys
            .Where(k => !k.IsDeleted &&
                        k.KeyType == "Temporary" &&
                        k.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(stoppingToken);

        if (expiredKeys.Count == 0)
        {
            _logger.LogInformation("没有需要清理的过期密钥");
            return;
        }

        _logger.LogInformation("找到 {Count} 个过期的临时密钥", expiredKeys.Count);

        var uploadsPath = Path.Combine(env.WebRootPath, "uploads");

        foreach (var key in expiredKeys)
        {
            try
            {
                // 查找关联的文件
                var relatedFiles = await dbContext.Files
                    .Where(f => f.ApiKeyId == key.Id)
                    .ToListAsync(stoppingToken);

                _logger.LogInformation("密钥 {KeyId} 关联了 {FileCount} 个文件", key.Id, relatedFiles.Count);

                // 删除物理文件
                foreach (var file in relatedFiles)
                {
                    var filePath = Path.Combine(uploadsPath, file.StoredFileName);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                        _logger.LogInformation("已删除物理文件: {FileName}", file.StoredFileName);
                    }
                }

                // 删除文件记录
                dbContext.Files.RemoveRange(relatedFiles);

                // 删除密钥
                dbContext.ApiKeys.Remove(key);

                _logger.LogInformation("已清理过期密钥: {KeyId}", key.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理密钥 {KeyId} 时发生错误", key.Id);
            }
        }

        await dbContext.SaveChangesAsync(stoppingToken);
        _logger.LogInformation("过期密钥清理完成");

        // 清理过期的 WS 客户端文件位置记录
        try
        {
            var expiredLocations = await dbContext.Set<FileUploadServer.Core.Entities.FileLocation>()
                .Where(fl => fl.ExpiresAt != null && fl.ExpiresAt < DateTime.UtcNow)
                .ToListAsync(stoppingToken);

            if (expiredLocations.Count > 0)
            {
                _logger.LogInformation("找到 {Count} 个过期的 WS 客户端文件位置记录", expiredLocations.Count);

                var wsStrategy = scope.ServiceProvider.GetRequiredService<WsStorageStrategy>();

                foreach (var location in expiredLocations)
                {
                    try
                    {
                        await wsStrategy.DeleteAsync(location.FilePath);
                        _logger.LogInformation("已删除 WS 客户端文件: {FilePath} (客户端: {ClientId})",
                            location.FilePath, location.ClientId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除 WS 客户端文件失败: {FilePath} (客户端: {ClientId}，可能已离线)",
                            location.FilePath, location.ClientId);
                    }

                    // 无论 WS 删除是否成功，都删除数据库记录
                    dbContext.Set<FileUploadServer.Core.Entities.FileLocation>().Remove(location);
                }

                await dbContext.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("过期 WS 客户端文件位置记录清理完成");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理过期WS客户端文件位置记录时发生错误");
        }
    }
}
