using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Encryption;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 文件下载共享服务。
/// <para>统一"读取文件流 + 透明解密"逻辑，供网页下载（Download.cshtml）、
/// API 下载（FileApiController.Download）、公共访问（PublicFileMiddleware）三处共用，
/// 确保加密文件在所有入口一致解密，杜绝解密逻辑分叉。</para>
/// </summary>
public class FileDownloadService
{
    private readonly IWebHostEnvironment _env;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileDownloadService> _logger;

    public FileDownloadService(
        IWebHostEnvironment env,
        IServiceScopeFactory scopeFactory,
        ILogger<FileDownloadService> logger)
    {
        _env = env;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 按文件元数据打开解密后的读取流（支持 WS 存储与本地存储）。
    /// 若文件加密且密钥版本可用，返回 <see cref="AesGcmDecryptStream"/> 透明解密流；
    /// 否则返回原始字节流（加密未初始化 / 密钥版本不支持时降级，与既有行为一致）。
    /// </summary>
    /// <param name="file">文件元数据</param>
    /// <returns>解密后的读取流，调用方负责 Dispose</returns>
    /// <exception cref="FileNotFoundException">本地文件不存在</exception>
    /// <exception cref="InvalidOperationException">WS 客户端不可用</exception>
    /// <exception cref="TimeoutException">从 WS 客户端读取超时</exception>
    public async Task<Stream> OpenDecryptedStreamAsync(FileItem file)
    {
        Stream rawStream;

        if (file.StorageMode == "WebSocket" && !string.IsNullOrEmpty(file.ClientId))
        {
            // WsStorageStrategy 是 scoped，经 scopeFactory 获取
            var wsStrategy = _scopeFactory.CreateScope()
                .ServiceProvider.GetRequiredService<WsStorageStrategy>();
            var remotePath = file.StoragePath ?? file.FileName;
            rawStream = await wsStrategy.ReadAsync(remotePath);
            _logger.LogDebug("从 WS 客户端读取文件: {ClientId}, {Path}", file.ClientId, remotePath);
        }
        else
        {
            var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
            var filePath = ResolveDiskPath(uploadsPath, file);
            if (!System.IO.File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}", filePath);
            }
            rawStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan);
            _logger.LogDebug("从本地磁盘读取文件: {FilePath}", filePath);
        }

        // 解密包装：加密不可用 / 密钥版本不支持时返回原始流（降级明文）
        IKeyProvider? keyProvider = null;
        try
        {
            keyProvider = _scopeFactory.CreateScope().ServiceProvider.GetService<IKeyProvider>();
        }
        catch
        {
            // 加密服务不可用，返回原始流
        }

        if (file.EncryptionVersion > 0 && keyProvider != null &&
            keyProvider.SupportsKeyVersion(file.KeyVersion))
        {
            _logger.LogDebug("透明解密下载: {FileName} (KeyVer={KeyVer})", file.FileName, file.KeyVersion);
            return new AesGcmDecryptStream(rawStream, keyProvider);
        }

        return rawStream;
    }

    /// <summary>
    /// 解析磁盘文件路径。
    /// <para>加密文件实际存储在子目录 + <see cref="FileItem.DiskFileName"/>（哈希命名），
    /// 明文文件使用 <see cref="FileItem.StoredFileName"/>。</para>
    /// </summary>
    public static string ResolveDiskPath(string uploadsPath, FileItem file)
    {
        if (file.EncryptionVersion > 0 && !string.IsNullOrEmpty(file.DiskFileName))
        {
            return Path.Combine(uploadsPath, file.DiskFileName[..2], file.DiskFileName);
        }
        return Path.Combine(uploadsPath, file.StoredFileName);
    }
}
