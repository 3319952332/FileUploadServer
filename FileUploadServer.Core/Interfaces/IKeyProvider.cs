namespace FileUploadServer.Core.Interfaces;

/// <summary>
/// 密钥提供者接口
/// 负责提供用于文件加密的 Master Key，支持密钥版本管理
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// 获取指定版本的 Master Key
    /// </summary>
    /// <param name="keyVersion">密钥版本号，默认为 1</param>
    /// <returns>32 字节的 Master Key</returns>
    /// <exception cref="KeyNotFoundException">当指定的密钥版本不受支持时抛出</exception>
    byte[] GetMasterKey(ushort keyVersion = 1);

    /// <summary>
    /// 当前使用的密钥版本号
    /// </summary>
    ushort CurrentKeyVersion { get; }

    /// <summary>
    /// 检查是否支持指定的密钥版本
    /// </summary>
    /// <param name="version">要检查的密钥版本号</param>
    /// <returns>如果支持则返回 true</returns>
    bool SupportsKeyVersion(ushort version);
}
