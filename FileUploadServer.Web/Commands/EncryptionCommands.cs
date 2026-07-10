using System.Security.Cryptography;
using System.Text;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Commands;

/// <summary>
/// 加密系统命令行工具
/// 在 Program.cs 中通过 args 触发
/// </summary>
public static class EncryptionCommands
{
    /// <summary>
    /// 尝试处理加密相关命令行参数
    /// </summary>
    /// <param name="args">命令行参数</param>
    /// <param name="serviceProvider">服务提供者</param>
    /// <returns>如果处理了加密命令则返回 true，否则返回 false</returns>
    public static async Task<bool> TryHandleAsync(string[] args, IServiceProvider serviceProvider)
    {
        if (args.Length == 0) return false;

        switch (args[0].ToLowerInvariant())
        {
            case "--encrypt-init":
                await HandleEncryptInitAsync(serviceProvider);
                return true;

            case "--recover":
                await HandleRecoverAsync(serviceProvider);
                return true;

            case "--encrypt-add-slot":
                await HandleAddSlotAsync(serviceProvider);
                return true;

            case "--encrypt-remove-slot":
                await HandleRemoveSlotAsync(serviceProvider, args);
                return true;

            case "--export-plaintext":
                await HandleExportPlaintextAsync(serviceProvider, args);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// --encrypt-init：交互式初始化加密系统
    /// 生成 Master Key，设置恢复口令
    /// </summary>
    private static async Task HandleEncryptInitAsync(IServiceProvider serviceProvider)
    {
        var logger = GetLogger(serviceProvider, nameof(HandleEncryptInitAsync));
        logger.LogInformation("=== 加密系统初始化 ===");

        var keyFilePath = GetKeyFilePath(serviceProvider);
        var slotManager = new KeySlotManager(keyFilePath, GetLogger<KeySlotManager>(serviceProvider));

        if (slotManager.SlotCount > 0)
        {
            logger.LogWarning("密钥文件已存在: {Path}", keyFilePath);
            Console.Write("密钥文件已存在。是否覆盖？(y/N): ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response != "y" && response != "yes")
            {
                logger.LogInformation("初始化已取消。");
                return;
            }
        }

        // 生成 Master Key 并初始化槽位
        var masterKey = slotManager.InitializeSlots();
        logger.LogInformation("✓ Master Key 已生成");

        // 设置恢复口令
        Console.WriteLine();
        Console.WriteLine("建议：设置一个或多个恢复口令，以便在密钥文件丢失时恢复数据。");
        Console.Write("是否设置恢复口令？(Y/n): ");
        var setupPassphrase = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (setupPassphrase != "n" && setupPassphrase != "no")
        {
            await SetupPassphraseInteractiveAsync(slotManager, logger);
        }

        // 显示摘要
        Console.WriteLine();
        logger.LogInformation("=== 初始化完成 ===");
        logger.LogInformation("密钥文件: {Path}", keyFilePath);
        logger.LogInformation("密钥版本: {Version}", slotManager.CurrentKeyVersion);
        logger.LogInformation("密钥槽数: {Count}", slotManager.SlotCount);
        Console.WriteLine();
        Console.WriteLine("⚠ 重要提示：请立即备份密钥文件和恢复口令！");
        Console.WriteLine("   - 密钥文件: {0}", keyFilePath);
        Console.WriteLine("   - 如果密钥文件和恢复口令全部丢失，数据将无法恢复！");

        // 清理内存中的密钥
        Array.Clear(masterKey, 0, masterKey.Length);
    }

    /// <summary>
    /// --recover：通过恢复口令重建密钥文件
    /// </summary>
    private static async Task HandleRecoverAsync(IServiceProvider serviceProvider)
    {
        var logger = GetLogger(serviceProvider, nameof(HandleRecoverAsync));
        logger.LogInformation("=== 通过恢复口令重建密钥 ===");

        var keyFilePath = GetKeyFilePath(serviceProvider);
        var slotManager = new KeySlotManager(keyFilePath, GetLogger<KeySlotManager>(serviceProvider));

        if (!File.Exists(keyFilePath))
        {
            logger.LogError("密钥文件不存在: {Path}", keyFilePath);
            Console.WriteLine("错误：密钥文件不存在。请先使用 --encrypt-init 初始化加密系统。");
            return;
        }

        Console.Write("请输入恢复口令: ");
        var passphrase = ReadPassword();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            logger.LogError("口令不能为空。");
            return;
        }

        try
        {
            var masterKey = slotManager.RecoverByPassphrase(passphrase, rebuildKeyFile: true);
            Array.Clear(masterKey, 0, masterKey.Length);
            logger.LogInformation("✓ 密钥恢复成功！密钥文件已重建。");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError("密钥恢复失败: {Message}", ex.Message);
            Console.WriteLine("错误：口令不正确或密钥文件已损坏。");
        }
    }

    /// <summary>
    /// --encrypt-add-slot：添加新恢复口令
    /// </summary>
    private static async Task HandleAddSlotAsync(IServiceProvider serviceProvider)
    {
        var logger = GetLogger(serviceProvider, nameof(HandleAddSlotAsync));
        logger.LogInformation("=== 添加恢复口令槽位 ===");

        var keyFilePath = GetKeyFilePath(serviceProvider);
        var slotManager = new KeySlotManager(keyFilePath, GetLogger<KeySlotManager>(serviceProvider));

        if (!File.Exists(keyFilePath))
        {
            logger.LogError("密钥文件不存在: {Path}", keyFilePath);
            Console.WriteLine("错误：密钥文件不存在。请先使用 --encrypt-init 初始化加密系统。");
            return;
        }

        Console.Write("请输入新的恢复口令: ");
        var passphrase = ReadPassword();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            logger.LogError("口令不能为空。");
            return;
        }

        Console.Write("请输入确认口令: ");
        var confirm = ReadPassword();
        Console.WriteLine();

        if (passphrase != confirm)
        {
            logger.LogError("两次输入的口令不一致。");
            return;
        }

        Console.Write("口令提示（可选，例如用于XXX的恢复口令）: ");
        var hint = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(hint))
        {
            hint = null;
        }

        try
        {
            int slotIndex = slotManager.AddPassphraseSlot(passphrase, hint);
            logger.LogInformation("✓ 恢复口令槽位已添加 (索引: {SlotIndex})", slotIndex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "添加恢复口令槽位失败。");
        }
    }

    /// <summary>
    /// --encrypt-remove-slot <index>：移除恢复口令
    /// </summary>
    private static async Task HandleRemoveSlotAsync(IServiceProvider serviceProvider, string[] args)
    {
        var logger = GetLogger(serviceProvider, nameof(HandleRemoveSlotAsync));
        logger.LogInformation("=== 移除密钥槽位 ===");

        if (args.Length < 2 || !int.TryParse(args[1], out int slotIndex))
        {
            Console.WriteLine("用法: --encrypt-remove-slot <索引>");
            Console.WriteLine("示例: --encrypt-remove-slot 1  # 移除第1号槽位（第一个恢复口令）");
            return;
        }

        var keyFilePath = GetKeyFilePath(serviceProvider);
        var slotManager = new KeySlotManager(keyFilePath, GetLogger<KeySlotManager>(serviceProvider));

        if (!File.Exists(keyFilePath))
        {
            logger.LogError("密钥文件不存在: {Path}", keyFilePath);
            Console.WriteLine("错误：密钥文件不存在。");
            return;
        }

        try
        {
            slotManager.RemoveSlot(slotIndex);
            logger.LogInformation("✓ 槽位 {SlotIndex} 已移除。", slotIndex);
        }
        catch (ArgumentOutOfRangeException)
        {
            logger.LogError("无效的槽位索引: {SlotIndex}。", slotIndex);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError("无法移除槽位: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// --export-plaintext <outputDir>：批量解密导出
    /// </summary>
    private static async Task HandleExportPlaintextAsync(IServiceProvider serviceProvider, string[] args)
    {
        var logger = GetLogger(serviceProvider, nameof(HandleExportPlaintextAsync));
        logger.LogInformation("=== 批量解密导出 ===");

        if (args.Length < 2)
        {
            Console.WriteLine("用法: --export-plaintext <输出目录>");
            Console.WriteLine("示例: --export-plaintext /backup/files");
            return;
        }

        var outputDir = args[1];
        if (!Directory.Exists(outputDir))
        {
            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "无法创建输出目录: {Path}", outputDir);
                Console.WriteLine($"错误：无法创建输出目录 {outputDir}");
                return;
            }
        }

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keyProvider = scope.ServiceProvider.GetRequiredService<IKeyProvider>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var uploadsPath = Path.Combine(env.WebRootPath, "uploads");

        var files = await dbContext.Files.ToListAsync();
        logger.LogInformation("找到 {Count} 个文件，开始导出...", files.Count);

        int successCount = 0;
        int failCount = 0;

        foreach (var file in files)
        {
            try
            {
                var diskFileName = !string.IsNullOrEmpty(file.DiskFileName)
                    ? file.DiskFileName
                    : file.StoredFileName;

                var filePath = FindFilePath(uploadsPath, diskFileName);
                if (!File.Exists(filePath))
                {
                    logger.LogWarning("文件不存在 (ID={FileId}): {Path}", file.Id, filePath);
                    failCount++;
                    continue;
                }

                // 构造输出文件名：{Id}_{原始文件名}
                var safeFileName = SanitizeFileName(file.FileName);
                var outputPath = Path.Combine(outputDir, $"{file.Id}_{safeFileName}");

                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                Stream decryptStream;

                // 判断是否为加密文件
                if (file.EncryptionVersion >= 1 && !string.IsNullOrEmpty(file.DiskFileName))
                {
                    decryptStream = new AesGcmDecryptStream(
                        fileStream, keyProvider, logger);
                }
                else
                {
                    // 未加密文件，直接复制
                    decryptStream = fileStream;
                }

                await using (decryptStream)
                await using (var outputStream = File.Create(outputPath))
                {
                    await decryptStream.CopyToAsync(outputStream);
                }

                successCount++;
                logger.LogDebug("已导出: {FileName} -> {OutputPath}", file.FileName, outputPath);
            }
            catch (Exception ex)
            {
                failCount++;
                logger.LogError(ex, "导出文件 (ID={FileId}) 失败。", file.Id);
            }
        }

        logger.LogInformation("导出完成。成功: {Success}, 失败: {Fail}", successCount, failCount);
        Console.WriteLine($"导出完成！{successCount} 个文件成功导出到 {outputDir}");
        if (failCount > 0)
        {
            Console.WriteLine($"警告: {failCount} 个文件导出失败，请查看日志了解详情。");
        }
    }

    /// <summary>
    /// 交互式设置恢复口令
    /// </summary>
    private static async Task SetupPassphraseInteractiveAsync(KeySlotManager slotManager, ILogger logger)
    {
        while (true)
        {
            Console.WriteLine();
            Console.Write("请输入恢复口令（留空结束）: ");
            var passphrase = ReadPassword();
            Console.WriteLine();

            if (string.IsNullOrWhiteSpace(passphrase))
            {
                logger.LogInformation("恢复口令设置完成。");
                break;
            }

            Console.Write("请再次输入确认: ");
            var confirm = ReadPassword();
            Console.WriteLine();

            if (passphrase != confirm)
            {
                logger.LogWarning("两次输入的口令不一致，请重新输入。");
                continue;
            }

            Console.Write("口令提示（可选）: ");
            var hint = Console.ReadLine()?.Trim();

            try
            {
                int slotIndex = slotManager.AddPassphraseSlot(passphrase, hint ?? null);
                logger.LogInformation("✓ 恢复口令槽位已添加 (索引: {SlotIndex})", slotIndex);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "添加恢复口令槽位失败。");
            }

            Console.WriteLine();
            Console.Write("是否继续添加另一个恢复口令？(y/N): ");
            var again = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (again != "y" && again != "yes")
            {
                break;
            }
        }
    }

    /// <summary>
    /// 从服务提供者获取日志记录器
    /// </summary>
    private static ILogger GetLogger(IServiceProvider serviceProvider, string categoryName)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        return loggerFactory.CreateLogger($"FileUploadServer.Web.Commands.{categoryName}");
    }

    /// <summary>
    /// 从服务提供者获取指定类型的日志记录器
    /// </summary>
    private static ILogger<T> GetLogger<T>(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<ILogger<T>>();
    }

    /// <summary>
    /// 从配置获取密钥文件路径
    /// </summary>
    private static string GetKeyFilePath(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return configuration.GetValue<string>("Encryption:KeyFilePath") ?? KeyProvider.DefaultKeyFilePath;
    }

    /// <summary>
    /// 读取密码（不在控制台回显）
    /// </summary>
    private static string ReadPassword()
    {
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return password.ToString();
    }

    /// <summary>
    /// 在 uploads 目录中查找文件（支持子目录格式）
    /// </summary>
    private static string FindFilePath(string uploadsPath, string fileName)
    {
        if (fileName.Length >= 2)
        {
            var subDirPath = Path.Combine(uploadsPath, fileName[..2], fileName);
            if (File.Exists(subDirPath))
                return subDirPath;
        }

        return Path.Combine(uploadsPath, fileName);
    }

    /// <summary>
    /// 清理文件名中的非法字符
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(fileName.Length);

        foreach (var c in fileName)
        {
            if (Array.IndexOf(invalidChars, c) < 0)
            {
                sanitized.Append(c);
            }
            else
            {
                sanitized.Append('_');
            }
        }

        return sanitized.ToString();
    }
}
