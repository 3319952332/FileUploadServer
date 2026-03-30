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
}
