using FileUploadServer.Core.Interfaces;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 本地存储策略。
/// 直接读写本地文件系统。支持可选的加密流封装。
/// </summary>
public class LocalStorageStrategy : IStorageStrategy
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalStorageStrategy> _logger;

    /// <summary>基础存储路径，默认 wwwroot/uploads。</summary>
    private string BasePath { get; }

    /// <summary>是否启用加密存储。</summary>
    private bool EncryptionEnabled { get; }

    public LocalStorageStrategy(
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<LocalStorageStrategy> logger)
    {
        _env = env;
        _configuration = configuration;
        _logger = logger;

        BasePath = configuration.GetValue<string>("Storage:LocalPath")
                   ?? Path.Combine(_env.WebRootPath, "uploads");
        EncryptionEnabled = configuration.GetValue<bool>("Encryption:Enabled");

        Directory.CreateDirectory(BasePath);
    }

    /// <summary>
    /// 读取文件流。
    /// 如果启用加密，返回解密流。
    /// </summary>
    public Task<Stream> ReadAsync(string path)
    {
        var filePath = ResolveFilePath(path);
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found: {FilePath}", filePath);
            throw new FileNotFoundException($"File not found: {path}", filePath);
        }

        _logger.LogDebug("Reading file: {FilePath}", filePath);

        Stream fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);

        // 如果加密启用，包装解密流
        // TODO: Phase 1.5 完成后集成 AesGcmDecryptStream
        // if (EncryptionEnabled)
        // {
        //     var keyProvider = _serviceProvider.GetRequiredService<IKeyProvider>();
        //     fileStream = new AesGcmDecryptStream(fileStream, keyProvider);
        // }

        return Task.FromResult(fileStream);
    }

    /// <summary>
    /// 写入文件流。
    /// 如果启用加密，返回加密流。
    /// </summary>
    public async Task WriteAsync(string path, Stream data)
    {
        var filePath = ResolveFilePath(path);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogDebug("Writing file: {FilePath}", filePath);

        Stream fileStream = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            65536, FileOptions.SequentialScan);

        // 如果加密启用，包装加密流
        // TODO: Phase 1.5 完成后集成 AesGcmEncryptStream
        // if (EncryptionEnabled)
        // {
        //     var keyProvider = _serviceProvider.GetRequiredService<IKeyProvider>();
        //     fileStream = new AesGcmEncryptStream(fileStream, keyProvider);
        // }

        try
        {
            await data.CopyToAsync(fileStream);
        }
        finally
        {
            await fileStream.DisposeAsync();
        }
    }

    /// <summary>
    /// 删除文件。
    /// </summary>
    public Task DeleteAsync(string path)
    {
        var filePath = ResolveFilePath(path);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted file: {FilePath}", filePath);

            // 尝试删除空目录
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        else
        {
            _logger.LogWarning("File not found for deletion: {FilePath}", filePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 将逻辑路径解析为本地文件路径。
    /// 例如：/public/doc/report.pdf → {BasePath}/public/doc/report.pdf
    /// </summary>
    private string ResolveFilePath(string path)
    {
        // 规范化路径：去掉开头的 /，防止路径遍历
        var normalized = path.TrimStart('/');
        normalized = normalized.Replace('/', Path.DirectorySeparatorChar);

        // 安全检查：禁止 .. 路径遍历
        if (normalized.Contains(".."))
        {
            throw new UnauthorizedAccessException($"Path traversal detected: {path}");
        }

        return Path.Combine(BasePath, normalized);
    }
}
