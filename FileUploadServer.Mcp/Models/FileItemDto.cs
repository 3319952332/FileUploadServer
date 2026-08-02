namespace FileUploadServer.Mcp.Models;

/// <summary>
/// 后端 FileItem 实体的 DTO（解析 GET /api/files 等接口返回的 JSON）。
/// 字段与 FileUploadServer.Core/Entities/FileItem.cs 一一对应。
/// </summary>
public sealed class FileItemDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public long FileSize { get; set; }
    public string ContentType { get; set; } = "";
    public DateTime UploadedAt { get; set; }
    public int? ApiKeyId { get; set; }
    public ushort EncryptionVersion { get; set; }
    public ushort KeyVersion { get; set; }
    public string DiskFileName { get; set; } = "";
    public string? FileHash { get; set; }
    public int BlockSize { get; set; }
    public bool IsPublic { get; set; }
    public string? PublicPath { get; set; }
    public string StorageMode { get; set; } = "Local";
    public string? ClientId { get; set; }
    public string? StoragePath { get; set; }
}
