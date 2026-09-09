using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileUploadServer.Auth.Data;

/// <summary>
/// 设计时工厂：仅供 dotnet ef 生成迁移使用，避免启动 Program.cs 连库。
/// 运行时连接串来自 appsettings.json，与此处无关。
/// </summary>
public class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        // 迁移生成不需要真实数据库连接；仅需 Provider 配置
        var conn = Environment.GetEnvironmentVariable("AUTH_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=fileupload;Username=postgres;Password=__design_time_only__";
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(conn, b => b.MigrationsHistoryTable("__EFMigrationsHistory_Auth"))
            .Options;
        return new AuthDbContext(options);
    }
}
