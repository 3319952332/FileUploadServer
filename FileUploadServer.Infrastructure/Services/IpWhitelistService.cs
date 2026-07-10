using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Infrastructure.Services;

/// <summary>
/// IP白名单服务
/// </summary>
public interface IIpWhitelistService
{
    /// <summary>
    /// 检查IP是否在白名单中
    /// </summary>
    /// <param name="ipAddress">IP地址</param>
    /// <returns>是否在白名单中</returns>
    Task<bool> IsIpAllowedAsync(string ipAddress);

    /// <summary>
    /// 获取所有白名单IP
    /// </summary>
    Task<List<IpWhitelist>> GetAllAsync();

    /// <summary>
    /// 添加IP到白名单
    /// </summary>
    Task AddAsync(string ipAddress, string description = "");

    /// <summary>
    /// 从白名单移除IP
    /// </summary>
    Task RemoveAsync(int id);
}

/// <summary>
/// IP白名单服务实现
/// </summary>
public class IpWhitelistService : IIpWhitelistService
{
    private readonly AppDbContext _dbContext;

    public IpWhitelistService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsIpAllowedAsync(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return false;
        }

        // 检查白名单中是否存在启用的该IP
        return await _dbContext.IpWhitelists
            .AnyAsync(w => w.IpAddress == ipAddress && w.IsEnabled);
    }

    public async Task<List<IpWhitelist>> GetAllAsync()
    {
        return await _dbContext.IpWhitelists
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();
    }

    public async Task AddAsync(string ipAddress, string description = "")
    {
        // 检查是否已存在
        var existing = await _dbContext.IpWhitelists
            .FirstOrDefaultAsync(w => w.IpAddress == ipAddress);

        if (existing != null)
        {
            // 如果已存在，重新启用
            existing.IsEnabled = true;
            existing.Description = description;
        }
        else
        {
            // 添加新IP
            var whitelist = new IpWhitelist
            {
                IpAddress = ipAddress,
                Description = description,
                CreatedAt = DateTime.UtcNow,
                IsEnabled = true
            };
            await _dbContext.IpWhitelists.AddAsync(whitelist);
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task RemoveAsync(int id)
    {
        var whitelist = await _dbContext.IpWhitelists.FindAsync(id);
        if (whitelist != null)
        {
            _dbContext.IpWhitelists.Remove(whitelist);
            await _dbContext.SaveChangesAsync();
        }
    }
}
