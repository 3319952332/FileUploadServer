namespace FileUploadServer.Core.Interfaces;

/// <summary>
/// 存储模式
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// 本地磁盘存储
    /// </summary>
    Local,

    /// <summary>
    /// WebSocket客户端远程存储
    /// </summary>
    WebSocket,

    /// <summary>
    /// 混合模式（按路径规则选择）
    /// </summary>
    Hybrid
}

/// <summary>
/// 存储策略接口
/// </summary>
public interface IStorageStrategy
{
    /// <summary>
    /// 读取文件
    /// </summary>
    Task<Stream> ReadAsync(string path);

    /// <summary>
    /// 写入文件
    /// </summary>
    Task WriteAsync(string path, Stream data);

    /// <summary>
    /// 删除文件
    /// </summary>
    Task DeleteAsync(string path);
}
