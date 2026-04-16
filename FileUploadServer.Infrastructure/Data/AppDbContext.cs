using FileUploadServer.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Infrastructure.Data;

/// <summary>
/// 数据库上下文
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// 文件表
    /// </summary>
    public DbSet<FileItem> Files { get; set; }

    /// <summary>
    /// API密钥表
    /// </summary>
    public DbSet<ApiKey> ApiKeys { get; set; }

    /// <summary>
    /// IP白名单表
    /// </summary>
    public DbSet<IpWhitelist> IpWhitelists { get; set; }
}
