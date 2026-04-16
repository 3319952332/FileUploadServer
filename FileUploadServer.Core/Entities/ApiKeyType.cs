namespace FileUploadServer.Core.Entities;

/// <summary>
/// API密钥类型
/// </summary>
public enum ApiKeyType
{
    /// <summary>
    /// 管理密钥 - 可管理所有文件
    /// </summary>
    Admin = 1,

    /// <summary>
    /// 临时密钥 - 只能访问自有文件，过期后文件自动删除
    /// </summary>
    Temporary = 2
}
