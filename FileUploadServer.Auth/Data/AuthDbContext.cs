using FileUploadServer.Auth.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Auth.Data;

/// <summary>
/// 登录服务器数据上下文（独立于文件服的 AppDbContext，同一 PostgreSQL 实例）
/// 使用独立迁移历史表，避免与文件服迁移记录冲突。
/// </summary>
public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<AuthUser> Users => Set<AuthUser>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<AuthAccountKey> AccountKeys => Set<AuthAccountKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthUser>(e =>
        {
            e.ToTable("AuthUsers");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(16);
        });

        modelBuilder.Entity<AuthSession>(e =>
        {
            e.ToTable("AuthSessions");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(128);
            e.HasOne<AuthUser>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthAccountKey>(e =>
        {
            e.ToTable("AuthAccountKeys");
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.FileKey).HasMaxLength(128);
            e.Property(x => x.KeyType).HasMaxLength(16);
            e.HasOne<AuthUser>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
