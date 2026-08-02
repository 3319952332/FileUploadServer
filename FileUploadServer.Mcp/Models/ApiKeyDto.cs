namespace FileUploadServer.Mcp.Models;

/// <summary>
/// 后端 ApiKey 实体的 DTO（解析申请密钥/密钥列表接口返回的 JSON）。
/// 字段与 FileUploadServer.Core/Entities/ApiKey.cs 一一对应。
/// </summary>
public sealed class ApiKeyDto
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsDeleted { get; set; }
    public string KeyType { get; set; } = "Temporary";
}
