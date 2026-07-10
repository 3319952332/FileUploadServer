using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Infrastructure.Repositories;

/// <summary>
/// FileLocation 仓储接口。
/// </summary>
public interface IFileLocationRepository
{
    /// <summary>根据 ID 获取 FileLocation。</summary>
    Task<FileLocation?> GetByIdAsync(Guid id);

    /// <summary>根据路径获取 FileLocation。</summary>
    Task<FileLocation?> GetByPathAsync(string filePath);

    /// <summary>获取指定客户端的所有 FileLocation。</summary>
    Task<List<FileLocation>> GetByClientIdAsync(string clientId);

    /// <summary>根据路径和客户端 ID 获取 FileLocation。</summary>
    Task<FileLocation?> GetByPathAndClientAsync(string filePath, string clientId);

    /// <summary>获取指定 API Key 的所有 FileLocation。</summary>
    Task<List<FileLocation>> GetByApiKeyIdAsync(int apiKeyId);

    /// <summary>添加 FileLocation。</summary>
    Task AddAsync(FileLocation fileLocation);

    /// <summary>批量添加 FileLocation。</summary>
    Task AddRangeAsync(IEnumerable<FileLocation> fileLocations);

    /// <summary>更新 FileLocation。</summary>
    Task UpdateAsync(FileLocation fileLocation);

    /// <summary>删除 FileLocation。</summary>
    Task DeleteAsync(FileLocation fileLocation);

    /// <summary>按条件查询 FileLocation（分页）。</summary>
    Task<(List<FileLocation> Items, int Total)> QueryAsync(
        string? clientId = null,
        string? pathPrefix = null,
        bool? isPublic = null,
        int skip = 0,
        int take = 100);

    /// <summary>获取指定客户端的文件总数和总大小。</summary>
    Task<(long TotalCount, long TotalSize)> GetClientStatsAsync(string clientId);

    /// <summary>删除所有过期的 FileLocation。</summary>
    Task<int> DeleteExpiredAsync();

    /// <summary>保存变更。</summary>
    Task SaveChangesAsync();
}

/// <summary>
/// FileLocation 仓储实现。
/// 提供对 FileLocation 实体的 CRUD 操作。
/// </summary>
public class FileLocationRepository : IFileLocationRepository
{
    private readonly AppDbContext _dbContext;

    public FileLocationRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc/>
    public async Task<FileLocation?> GetByIdAsync(Guid id)
    {
        return await _dbContext.Set<FileLocation>().FindAsync(id);
    }

    /// <inheritdoc/>
    public async Task<FileLocation?> GetByPathAsync(string filePath)
    {
        return await _dbContext.Set<FileLocation>()
            .FirstOrDefaultAsync(fl => fl.FilePath == filePath);
    }

    /// <inheritdoc/>
    public async Task<List<FileLocation>> GetByClientIdAsync(string clientId)
    {
        return await _dbContext.Set<FileLocation>()
            .Where(fl => fl.ClientId == clientId)
            .OrderByDescending(fl => fl.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task<FileLocation?> GetByPathAndClientAsync(string filePath, string clientId)
    {
        return await _dbContext.Set<FileLocation>()
            .FirstOrDefaultAsync(fl => fl.FilePath == filePath && fl.ClientId == clientId);
    }

    /// <inheritdoc/>
    public async Task<List<FileLocation>> GetByApiKeyIdAsync(int apiKeyId)
    {
        return await _dbContext.Set<FileLocation>()
            .Where(fl => fl.ApiKeyId == apiKeyId)
            .OrderByDescending(fl => fl.CreatedAt)
            .ToListAsync();
    }

    /// <inheritdoc/>
    public async Task AddAsync(FileLocation fileLocation)
    {
        await _dbContext.Set<FileLocation>().AddAsync(fileLocation);
        await _dbContext.SaveChangesAsync();
    }

    /// <inheritdoc/>
    public async Task AddRangeAsync(IEnumerable<FileLocation> fileLocations)
    {
        await _dbContext.Set<FileLocation>().AddRangeAsync(fileLocations);
        await _dbContext.SaveChangesAsync();
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(FileLocation fileLocation)
    {
        _dbContext.Set<FileLocation>().Update(fileLocation);
        await _dbContext.SaveChangesAsync();
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(FileLocation fileLocation)
    {
        _dbContext.Set<FileLocation>().Remove(fileLocation);
        await _dbContext.SaveChangesAsync();
    }

    /// <inheritdoc/>
    public async Task<(List<FileLocation> Items, int Total)> QueryAsync(
        string? clientId = null,
        string? pathPrefix = null,
        bool? isPublic = null,
        int skip = 0,
        int take = 100)
    {
        var query = _dbContext.Set<FileLocation>().AsQueryable();

        if (!string.IsNullOrEmpty(clientId))
        {
            query = query.Where(fl => fl.ClientId == clientId);
        }

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            query = query.Where(fl => fl.FilePath.StartsWith(pathPrefix));
        }

        if (isPublic.HasValue)
        {
            query = query.Where(fl => fl.IsPublic == isPublic.Value);
        }

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(fl => fl.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return (items, total);
    }

    /// <inheritdoc/>
    public async Task<(long TotalCount, long TotalSize)> GetClientStatsAsync(string clientId)
    {
        var query = _dbContext.Set<FileLocation>().Where(fl => fl.ClientId == clientId);
        var totalCount = await query.CountAsync();
        var totalSize = await query.SumAsync(fl => fl.FileSize);
        return (totalCount, totalSize);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteExpiredAsync()
    {
        var now = DateTime.UtcNow;
        var expired = await _dbContext.Set<FileLocation>()
            .Where(fl => fl.ExpiresAt != null && fl.ExpiresAt < now)
            .ToListAsync();

        if (expired.Count > 0)
        {
            _dbContext.Set<FileLocation>().RemoveRange(expired);
            await _dbContext.SaveChangesAsync();
        }

        return expired.Count;
    }

    /// <inheritdoc/>
    public async Task SaveChangesAsync()
    {
        await _dbContext.SaveChangesAsync();
    }
}
