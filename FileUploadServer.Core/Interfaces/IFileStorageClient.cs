namespace FileUploadServer.Core.Interfaces;

/// <summary>
/// 断开连接事件参数
/// </summary>
public class DisconnectEventArgs : EventArgs
{
    /// <summary>
    /// 断开原因
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// 是否将自动重连
    /// </summary>
    public bool WillReconnect { get; set; }
}

/// <summary>
/// 客户端存储接口
/// </summary>
public interface IFileStorageClient
{
    /// <summary>
    /// 连接到服务端
    /// </summary>
    Task ConnectAsync(string serverUrl, string clientId, string clientSecret);

    /// <summary>
    /// 断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// 是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 断开连接事件
    /// </summary>
    event EventHandler<DisconnectEventArgs>? OnDisconnected;

    /// <summary>
    /// 读取文件
    /// </summary>
    Task<Stream> ReadFileAsync(string path);

    /// <summary>
    /// 写入文件
    /// </summary>
    Task WriteFileAsync(string path, Stream data);

    /// <summary>
    /// 删除文件
    /// </summary>
    Task DeleteFileAsync(string path);

    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    Task<bool> FileExistsAsync(string path);

    /// <summary>
    /// 获取文件大小
    /// </summary>
    Task<long> GetFileSizeAsync(string path);

    /// <summary>
    /// 获取文件哈希
    /// </summary>
    Task<string> GetFileHashAsync(string path);
}
