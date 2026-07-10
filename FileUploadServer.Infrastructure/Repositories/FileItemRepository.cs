using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Infrastructure.Repositories;

/// <summary>
/// 文件仓储实现
/// </summary>
public class FileItemRepository : IFileItemRepository
{
    private readonly AppDbContext _context;

    public FileItemRepository(AppDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<List<FileItem>> GetAllAsync()
    {
        return await _context.Files
            .OrderByDescending(f => f.UploadedAt)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<FileItem?> GetByIdAsync(int id)
    {
        return await _context.Files.FindAsync(id);
    }

    /// <inheritdoc />
    public async Task AddAsync(FileItem fileItem)
    {
        await _context.Files.AddAsync(fileItem);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(FileItem fileItem)
    {
        _context.Files.Remove(fileItem);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public IQueryable<FileItem> GetQueryable()
    {
        return _context.Files.AsQueryable();
    }

    /// <inheritdoc />
    public async Task<FileItem?> GetByPublicPathAsync(string publicPath)
    {
        return await _context.Files
            .Where(f => f.IsPublic && f.PublicPath == publicPath)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<FileItem>> GetPublicFilesAsync(int page, int pageSize)
    {
        return await _context.Files
            .Where(f => f.IsPublic)
            .OrderByDescending(f => f.UploadedAt)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> GetPublicFilesCountAsync()
    {
        return await _context.Files
            .Where(f => f.IsPublic)
            .CountAsync();
    }

    /// <inheritdoc />
    public async Task<FileItem?> GetByDiskFileNameAsync(string diskFileName)
    {
        return await _context.Files
            .Where(f => f.DiskFileName == diskFileName)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<FileItem>> GetFilesByKeyVersionAsync(ushort keyVersion, int page, int pageSize)
    {
        return await _context.Files
            .Where(f => f.KeyVersion == keyVersion)
            .OrderByDescending(f => f.UploadedAt)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }
}
