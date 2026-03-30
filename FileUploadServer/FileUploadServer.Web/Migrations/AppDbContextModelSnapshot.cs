using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;

#nullable disable

namespace FileUploadServer.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
internal partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.Entity<FileUploadServer.Core.Entities.FileItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ContentType)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(e => e.FileName)
                .IsRequired()
                .HasMaxLength(255);

            entity.Property(e => e.FileSize)
                .IsRequired();

            entity.Property(e => e.StoredFileName)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(e => e.UploadedAt)
                .IsRequired();
        });
#pragma warning restore 612, 618
    }
}
