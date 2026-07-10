using FileUploadServer.Core.Entities;

namespace FileUploadServer.Core.Interfaces;

/// <summary>
/// 文件仓储接口
/// </summary>
public interface IFileItemRepository
{
    /// <summary>
    /// 获取所有文件列表
    /// </summary>
    Task<List<FileItem>> GetAllAsync();

    /// <summary>
    /// 根据ID获取文件
    /// </summary>
    Task<FileItem?> GetByIdAsync(int id);

    /// <summary>
    /// 添加文件
    /// </summary>
    Task AddAsync(FileItem fileItem);

    /// <summary>
    /// 删除文件
    /// </summary>
    Task DeleteAsync(FileItem fileItem);

    /// <summary>
    /// 保存更改
    /// </summary>
    Task SaveChangesAsync();

    /// <summary>
    /// 获取可查询的文件集合（用于权限过滤）
    /// </summary>
    IQueryable<FileItem> GetQueryable();

    /// <summary>
    /// 根据公共访问路径查找公开文件
    /// </summary>
    /// <param name="publicPath">公共访问路径</param>
    /// <returns>匹配的公开文件，未找到则返回 null</returns>
    Task<FileItem?> GetByPublicPathAsync(string publicPath);

    /// <summary>
    /// 分页查询所有公开文件
    /// </summary>
    /// <param name="page">页码（从 0 开始）</param>
    /// <param name="pageSize">每页条数</param>
    /// <returns>公开文件列表</returns>
    Task<List<FileItem>> GetPublicFilesAsync(int page, int pageSize);

    /// <summary>
    /// 获取公开文件总数
    /// </summary>
    /// <returns>公开文件总数</returns>
    Task<int> GetPublicFilesCountAsync();

    /// <summary>
    /// 根据磁盘文件名查找文件
    /// </summary>
    /// <param name="diskFileName">磁盘文件名</param>
    /// <returns>匹配的文件，未找到则返回 null</returns>
    Task<FileItem?> GetByDiskFileNameAsync(string diskFileName);

    /// <summary>
    /// 根据密钥版本分页查询文件（用于密钥轮换迁移）
    /// </summary>
    /// <param name="keyVersion">密钥版本号</param>
    /// <param name="page">页码（从 0 开始）</param>
    /// <param name="pageSize">每页条数</param>
    /// <returns>匹配的文件列表</returns>
    Task<List<FileItem>> GetFilesByKeyVersionAsync(ushort keyVersion, int page, int pageSize);
}
