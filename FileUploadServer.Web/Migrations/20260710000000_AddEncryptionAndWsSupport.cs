using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FileUploadServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptionAndWsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // 1. Create missing tables (ApiKeys, IpWhitelists)
            //    These were added to the code model but never had a migration.
            // ============================================================

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KeyType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpWhitelists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpAddress = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpWhitelists", x => x.Id);
                });

            // ============================================================
            // 2. Add new columns to existing Files table
            //    (encryption, public access, WS storage fields)
            // ============================================================

            migrationBuilder.AddColumn<int>(
                name: "ApiKeyId",
                table: "Files",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EncryptionVersion",
                table: "Files",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "KeyVersion",
                table: "Files",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "DiskFileName",
                table: "Files",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "Files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlockSize",
                table: "Files",
                type: "integer",
                nullable: false,
                defaultValue: 1048576);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "Files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicPath",
                table: "Files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageMode",
                table: "Files",
                type: "text",
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "Files",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoragePath",
                table: "Files",
                type: "text",
                nullable: true);

            // ============================================================
            // 3. Create new tables (WsClients, FileLocations)
            // ============================================================

            migrationBuilder.CreateTable(
                name: "WsClients",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ClientSecretHash = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PathPrefixes = table.Column<string>(type: "text", nullable: false),
                    StorageCapacity = table.Column<long>(type: "bigint", nullable: false),
                    CurrentStorage = table.Column<long>(type: "bigint", nullable: false),
                    LastConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WsClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileHash = table.Column<string>(type: "text", nullable: true),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    ApiKeyId = table.Column<int>(type: "integer", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileLocations", x => x.Id);
                });

            // ============================================================
            // 4. Create indices and foreign keys
            // ============================================================

            migrationBuilder.CreateIndex(
                name: "IX_Files_ApiKeyId",
                table: "Files",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_DiskFileName",
                table: "Files",
                column: "DiskFileName");

            migrationBuilder.CreateIndex(
                name: "IX_Files_IsPublic",
                table: "Files",
                column: "IsPublic");

            migrationBuilder.CreateIndex(
                name: "IX_FileLocations_FilePath",
                table: "FileLocations",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_FileLocations_ClientId",
                table: "FileLocations",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_FileLocations_IsPublic",
                table: "FileLocations",
                column: "IsPublic");

            migrationBuilder.AddForeignKey(
                name: "FK_Files_ApiKeys_ApiKeyId",
                table: "Files",
                column: "ApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove FK and indices
            migrationBuilder.DropForeignKey(
                name: "FK_Files_ApiKeys_ApiKeyId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_ApiKeyId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_DiskFileName",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_IsPublic",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_FileLocations_FilePath",
                table: "FileLocations");

            migrationBuilder.DropIndex(
                name: "IX_FileLocations_ClientId",
                table: "FileLocations");

            migrationBuilder.DropIndex(
                name: "IX_FileLocations_IsPublic",
                table: "FileLocations");

            // Drop new tables
            migrationBuilder.DropTable(
                name: "FileLocations");

            migrationBuilder.DropTable(
                name: "WsClients");

            // Remove new columns from Files
            migrationBuilder.DropColumn(
                name: "StoragePath",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "StorageMode",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PublicPath",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "BlockSize",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "DiskFileName",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "KeyVersion",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "EncryptionVersion",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "Files");

            // Drop previously missing tables
            migrationBuilder.DropTable(
                name: "IpWhitelists");

            migrationBuilder.DropTable(
                name: "ApiKeys");
        }
    }
}
