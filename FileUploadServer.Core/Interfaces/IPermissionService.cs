using FileUploadServer.Core.Entities;

namespace FileUploadServer.Core.Interfaces;

/// <summary>
/// 权限服务接口
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// 检查是否可以访问指定文件
    /// </summary>
    /// <param name="fileId">文件ID</param>
    /// <param name="currentKey">当前API密钥</param>
    /// <returns>是否可以访问</returns>
    Task<bool> CanAccessFileAsync(int fileId, ApiKey currentKey);

    /// <summary>
    /// 获取当前密钥可访问的文件查询
    /// </summary>
    /// <param name="currentKey">当前API密钥</param>
    /// <param name="allFiles">所有文件查询</param>
    /// <returns>过滤后的文件查询</returns>
    IQueryable<FileItem> GetAccessibleFilesQuery(ApiKey currentKey, IQueryable<FileItem> allFiles);
}
