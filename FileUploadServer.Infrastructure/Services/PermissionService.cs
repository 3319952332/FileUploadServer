using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Infrastructure.Services;

/// <summary>
/// 权限服务实现
/// </summary>
public class PermissionService : IPermissionService
{
    private readonly AppDbContext _dbContext;

    public PermissionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<bool> CanAccessFileAsync(int fileId, ApiKey currentKey)
    {
        // Admin类型的密钥可以访问所有文件
        if (currentKey.KeyType == "Admin")
        {
            return true;
        }

        // Temporary类型的密钥只能访问自己上传的文件
        var file = await _dbContext.Files.FindAsync(fileId);
        if (file == null)
        {
            return false;
        }

        return file.ApiKeyId == currentKey.Id;
    }

    /// <inheritdoc />
    public IQueryable<FileItem> GetAccessibleFilesQuery(ApiKey currentKey, IQueryable<FileItem> allFiles)
    {
        // Admin类型的密钥可以访问所有文件
        if (currentKey.KeyType == "Admin")
        {
            return allFiles;
        }

        // Temporary类型的密钥只能访问自己上传的文件
        return allFiles.Where(f => f.ApiKeyId == currentKey.Id);
    }
}
