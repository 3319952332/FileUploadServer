using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 密钥轮换配置选项
/// </summary>
public class KeyRotationOptions
{
    /// <summary>
    /// 轮换检查间隔（默认 24 小时）
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 迁移速率（每分钟处理的最大文件数，默认 100）
    /// </summary>
    public int MigrationRatePerMinute { get; set; } = 100;

    /// <summary>
    /// 是否启用自动密钥轮换
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 密钥轮换触发天数（生成新密钥后旧密钥仍可使用此天数）
    /// </summary>
    public int KeyRotationDays { get; set; } = 90;

    /// <summary>
    /// 密钥文件路径
    /// </summary>
    public string? KeyFilePath { get; set; }
}

/// <summary>
/// 密钥轮换后台服务
/// 定期检查并执行密钥轮换，逐步用新密钥重新加密旧版本文件
/// </summary>
public class KeyRotationService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<KeyRotationService> _logger;
    private readonly IOptions<KeyRotationOptions> _options;

    /// <summary>
    /// 初始化密钥轮换服务
    /// </summary>
    public KeyRotationService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<KeyRotationOptions> options,
        ILogger<KeyRotationService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Key rotation service is disabled.");
            return;
        }

        _logger.LogInformation(
            "Key rotation service started. CheckInterval={Interval}, MigrationRate={Rate}/min",
            _options.Value.CheckInterval, _options.Value.MigrationRatePerMinute);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformKeyRotationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during key rotation cycle.");
            }

            await Task.Delay(_options.Value.CheckInterval, stoppingToken);
        }
    }

    /// <summary>
    /// 执行密钥轮换
    /// </summary>
    private async Task PerformKeyRotationAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting key rotation check...");

        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keyProvider = scope.ServiceProvider.GetRequiredService<IKeyProvider>();
        var keySlotManager = scope.ServiceProvider.GetRequiredService<KeySlotManager>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        ushort currentKeyVersion = keyProvider.CurrentKeyVersion;

        // 检查是否有文件使用旧密钥版本
        var oldVersionFiles = await dbContext.Files
            .Where(f => f.EncryptionVersion != 1) // 0 = 未加密
            .ToListAsync(stoppingToken);

        // 过滤出使用旧密钥版本的文件（由具体业务逻辑判断）
        var filesToReEncrypt = oldVersionFiles
            .Where(f => f.KeyVersion != currentKeyVersion)
            .Take(_options.Value.MigrationRatePerMinute)
            .ToList();

        if (filesToReEncrypt.Count == 0)
        {
            _logger.LogInformation(
                "No files need re-encryption. Current key version: {KeyVersion}.",
                currentKeyVersion);
            return;
        }

        _logger.LogInformation(
            "Found {Count} files using old key version. Current version: {KeyVersion}. Starting re-encryption...",
            filesToReEncrypt.Count, currentKeyVersion);

        int successCount = 0;
        int failCount = 0;
        var uploadsPath = Path.Combine(env.WebRootPath, "uploads");

        foreach (var file in filesToReEncrypt)
        {
            try
            {
                stoppingToken.ThrowIfCancellationRequested();

                await ReEncryptFileAsync(
                    file, currentKeyVersion, keyProvider, dbContext, uploadsPath, stoppingToken);

                successCount++;
                _logger.LogDebug(
                    "Re-encrypted file {FileId} ({FileName}) to key version {KeyVersion}.",
                    file.Id, file.FileName, currentKeyVersion);
            }
            catch (Exception ex)
            {
                failCount++;
                _logger.LogError(ex, "Failed to re-encrypt file {FileId} ({FileName}).", file.Id, file.FileName);
            }

            // 迁移速率控制
            if (successCount % 10 == 0 && successCount > 0)
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(1.0) / _options.Value.MigrationRatePerMinute * 10,
                    stoppingToken);
            }
        }

        _logger.LogInformation(
            "Key rotation cycle completed. Success: {Success}, Failed: {Fail}.",
            successCount, failCount);
    }

    /// <summary>
    /// 重新加密单个文件
    /// </summary>
    private async Task ReEncryptFileAsync(
        Core.Entities.FileItem file,
        ushort newKeyVersion,
        IKeyProvider keyProvider,
        AppDbContext dbContext,
        string uploadsPath,
        CancellationToken stoppingToken)
    {
        // 确定磁盘文件路径
        var oldDiskFileName = file.DiskFileName;
        if (string.IsNullOrEmpty(oldDiskFileName))
        {
            // 旧文件使用 StoredFileName
            oldDiskFileName = file.StoredFileName;
        }

        var oldFilePath = FindFilePath(uploadsPath, oldDiskFileName);
        if (!File.Exists(oldFilePath))
        {
            _logger.LogWarning(
                "Physical file not found for FileId={FileId}: {FilePath}. Skipping.",
                file.Id, oldFilePath);
            return;
        }

        var newMasterKey = keyProvider.GetMasterKey(newKeyVersion);

        // 使用旧版密钥读取文件（解密流）
        // 读取整个文件到内存（支持小文件）或使用临时文件
        // 这里使用内存流，对于大文件应考虑临时文件
        byte[] plaintext;
        {
            using var oldFileStream = new FileStream(oldFilePath, FileMode.Open, FileAccess.Read);
            using var decryptStream = new AesGcmDecryptStream(
                oldFileStream, keyProvider, _logger);
            using var memoryStream = new MemoryStream();
            await decryptStream.CopyToAsync(memoryStream, stoppingToken);
            plaintext = memoryStream.ToArray();
        }

        // 使用新密钥重新加密
        var newDiskFileName = ComputeDiskFileName(file.Id, newMasterKey);
        var subDir = newDiskFileName[..2];
        var newDirPath = Path.Combine(uploadsPath, subDir);
        Directory.CreateDirectory(newDirPath);
        var newFilePath = Path.Combine(newDirPath, newDiskFileName);

        {
            using var newFileStream = new FileStream(newFilePath, FileMode.Create, FileAccess.Write);
            using var encryptStream = new AesGcmEncryptStream(
                newFileStream, newMasterKey, newKeyVersion,
                EncryptedFileConstants.DefaultBlockSize, _logger);
            await encryptStream.WriteAsync(plaintext, stoppingToken);
            await encryptStream.FlushAsync(stoppingToken);
        }

        // 删除旧文件
        if (!string.IsNullOrEmpty(file.DiskFileName) && file.DiskFileName != oldDiskFileName)
        {
            var oldDirPath = Path.Combine(uploadsPath, file.DiskFileName[..2]);
            var oldFullPath = Path.Combine(oldDirPath, file.DiskFileName);
            if (File.Exists(oldFullPath))
            {
                File.Delete(oldFullPath);
            }
        }
        else if (string.IsNullOrEmpty(file.DiskFileName))
        {
            // 旧版文件（使用 StoredFileName）
            File.Delete(oldFilePath);
        }

        // 更新数据库记录
        file.DiskFileName = newDiskFileName;
        file.KeyVersion = newKeyVersion;
        file.EncryptionVersion = 1;
        file.BlockSize = EncryptedFileConstants.DefaultBlockSize;

        dbContext.Files.Update(file);
        await dbContext.SaveChangesAsync(stoppingToken);

        _logger.LogDebug(
            "File {FileId} re-encrypted: OldKey={OldKeyVersion}, NewKey={NewKeyVersion}.",
            file.Id, file.KeyVersion, newKeyVersion);
    }

    /// <summary>
    /// 在 uploads 目录中查找文件（支持子目录格式）
    /// </summary>
    private static string FindFilePath(string uploadsPath, string fileName)
    {
        // 尝试子目录格式：uploads/{prefix2}/{fileName}
        if (fileName.Length >= 2)
        {
            var subDirPath = Path.Combine(uploadsPath, fileName[..2], fileName);
            if (File.Exists(subDirPath))
                return subDirPath;
        }

        // 回退到平铺格式：uploads/{fileName}
        return Path.Combine(uploadsPath, fileName);
    }

    /// <summary>
    /// 计算磁盘文件名
    /// diskFileName = SHA256(fileId + masterKeyPrefix)[0..32].ToHex()
    /// </summary>
    private static string ComputeDiskFileName(int fileId, byte[] masterKey)
    {
        var prefix = Convert.ToHexString(masterKey)[..16];
        var input = $"{fileId}:{prefix}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
