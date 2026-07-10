using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileUploadServer.Infrastructure;

/// <summary>
/// EF Core 设计时 DbContext 工厂，专用于迁移脚本生成。
/// 绕过运行时 DI 容器，使用占位连接字符串。
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=fileupload;Username=postgres;Password=postgres");
        return new AppDbContext(optionsBuilder.Options);
    }
}
