using System.Security.Cryptography;
using FileUploadServer.Core.Interfaces;

namespace FileUploadServer.WsClient.Storage;

/// <summary>
/// 本地文件存储客户端
/// 用于降级模式 / 单机部署 / 测试。
/// 文件操作直接映射到本地磁盘 I/O。
/// </summary>
public class LocalFileStorageClient : IFileStorageClient
{
    private readonly string _basePath;

    /// <summary>
    /// 是否已连接（本地模式始终为 true）
    /// </summary>
    public bool IsConnected => true;

    /// <summary>
    /// 断开连接事件（本地模式不会触发）
    /// </summary>
    #pragma warning disable CS0067 // 本地模式不会触发断开事件，为接口兼容保留
    public event EventHandler<DisconnectEventArgs>? OnDisconnected;
    #pragma warning restore CS0067

    /// <summary>
    /// 创建 LocalFileStorageClient 实例
    /// </summary>
    /// <param name="basePath">文件存储根目录</param>
    /// <exception cref="ArgumentNullException">basePath 为 null 时抛出</exception>
    public LocalFileStorageClient(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        _basePath = Path.GetFullPath(basePath);
    }

    /// <summary>
    /// 连接（本地模式无操作）
    /// </summary>
    public Task ConnectAsync(string serverUrl, string clientId, string clientSecret)
    {
        // 本地模式无需连接
        return Task.CompletedTask;
    }

    /// <summary>
    /// 断开连接（本地模式无操作）
    /// </summary>
    public Task DisconnectAsync()
    {
        // 本地模式无需断开
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取文件
    /// </summary>
    public Task<Stream> ReadFileAsync(string path)
    {
        var fullPath = GetSafePath(path);
        EnsureFileExists(fullPath);
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    /// <summary>
    /// 写入文件
    /// </summary>
    public async Task WriteFileAsync(string path, Stream data)
    {
        var fullPath = GetSafePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var fileStream = File.Create(fullPath);
        await using (fileStream.ConfigureAwait(false))
        {
            await data.CopyToAsync(fileStream).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 删除文件
    /// </summary>
    public Task DeleteFileAsync(string path)
    {
        var fullPath = GetSafePath(path);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    public Task<bool> FileExistsAsync(string path)
    {
        var fullPath = GetSafePath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    /// <summary>
    /// 获取文件大小
    /// </summary>
    public Task<long> GetFileSizeAsync(string path)
    {
        var fullPath = GetSafePath(path);
        EnsureFileExists(fullPath);
        return Task.FromResult(new FileInfo(fullPath).Length);
    }

    /// <summary>
    /// 获取文件的 SHA256 哈希
    /// </summary>
    public async Task<string> GetFileHashAsync(string path)
    {
        var fullPath = GetSafePath(path);
        EnsureFileExists(fullPath);

        await using var stream = File.OpenRead(fullPath);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 获取安全的完整路径，防止路径遍历攻击
    /// </summary>
    private string GetSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path cannot be empty", nameof(relativePath));

        // 必须以 / 开头
        if (!relativePath.StartsWith('/'))
            throw new ArgumentException("Path must start with '/'", nameof(relativePath));

        // 规范化路径
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(_basePath, normalized));

        // 安全检查：确保解析后的路径仍在 _basePath 下
        if (!fullPath.StartsWith(_basePath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Path traversal detected: {relativePath}");

        // 不能包含空字符
        if (relativePath.Contains('\0'))
            throw new ArgumentException("Path contains null characters", nameof(relativePath));

        // 路径长度限制
        if (relativePath.Length > 1024)
            throw new ArgumentException("Path too long (max 1024 characters)", nameof(relativePath));

        return fullPath;
    }

    /// <summary>
    /// 确保文件存在
    /// </summary>
    private static void EnsureFileExists(string fullPath)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File not found", fullPath);
    }
}
