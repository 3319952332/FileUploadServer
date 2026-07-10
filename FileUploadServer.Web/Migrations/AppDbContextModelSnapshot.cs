using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FileUploadServer.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
internal partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.5")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity<FileUploadServer.Core.Entities.ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);

            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(entity.Property(e => e.Id));

            entity.Property(e => e.Key).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ExpiresAt).IsRequired();
            entity.Property(e => e.IsDeleted).IsRequired();
            entity.Property(e => e.KeyType).IsRequired();
        });

        modelBuilder.Entity<FileUploadServer.Core.Entities.FileItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(entity.Property(e => e.Id));

            entity.Property(e => e.FileName).IsRequired();
            entity.Property(e => e.StoredFileName).IsRequired();
            entity.Property(e => e.FileSize).IsRequired();
            entity.Property(e => e.ContentType).IsRequired();
            entity.Property(e => e.UploadedAt).IsRequired();

            entity.Property(e => e.ApiKeyId).IsRequired(false);

            // Encryption fields
            entity.Property(e => e.EncryptionVersion).IsRequired();
            entity.Property(e => e.KeyVersion).IsRequired();
            entity.Property(e => e.DiskFileName).IsRequired();
            entity.Property(e => e.FileHash).IsRequired(false);
            entity.Property(e => e.BlockSize).IsRequired();

            // Public access fields
            entity.Property(e => e.IsPublic).IsRequired();
            entity.Property(e => e.PublicPath).IsRequired(false);

            // WS storage fields
            entity.Property(e => e.StorageMode).IsRequired();
            entity.Property(e => e.ClientId).IsRequired(false);
            entity.Property(e => e.StoragePath).IsRequired(false);

            entity.HasOne(e => e.ApiKey)
                .WithMany()
                .HasForeignKey(e => e.ApiKeyId);

            entity.HasIndex(e => e.DiskFileName);
            entity.HasIndex(e => e.IsPublic);
        });

        modelBuilder.Entity<FileUploadServer.Core.Entities.FileLocation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.FileName).IsRequired();
            entity.Property(e => e.FileSize).IsRequired();
            entity.Property(e => e.ClientId).IsRequired();
            entity.Property(e => e.ApiKeyId).IsRequired();
            entity.Property(e => e.IsPublic).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasIndex(e => e.FilePath);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.IsPublic);
        });

        modelBuilder.Entity<FileUploadServer.Core.Entities.IpWhitelist>(entity =>
        {
            entity.HasKey(e => e.Id);

            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(entity.Property(e => e.Id));

            entity.Property(e => e.IpAddress).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.IsEnabled).IsRequired();
        });

        modelBuilder.Entity<FileUploadServer.Core.Entities.WsClient>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ClientSecretHash).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.IsEnabled).IsRequired();
            entity.Property(e => e.PathPrefixes).IsRequired();
            entity.Property(e => e.StorageCapacity).IsRequired();
            entity.Property(e => e.CurrentStorage).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });
#pragma warning restore 612, 618
    }
}
