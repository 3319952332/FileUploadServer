namespace FileUploadServer.Core.Entities;

/// <summary>
/// 文件信息实体
/// </summary>
public class FileItem
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 原始文件名（用户上传时的文件名）
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 存储在服务器上的文件名（GUID避免重名）
    /// </summary>
    public string StoredFileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Content-Type MIME类型
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// 上传时间
    /// </summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 关联的API密钥ID
    /// </summary>
    public int? ApiKeyId { get; set; }

    /// <summary>
    /// 导航属性：关联的API密钥
    /// </summary>
    public ApiKey? ApiKey { get; set; }

    // ===== 加密相关字段 =====

    /// <summary>
    /// 加密格式版本（当前为1）
    /// </summary>
    public ushort EncryptionVersion { get; set; } = 1;

    /// <summary>
    /// 密钥版本号，用于支持密钥轮换
    /// </summary>
    public ushort KeyVersion { get; set; } = 1;

    /// <summary>
    /// 磁盘文件名（SHA256(fileId + keyPrefix) 的十六进制表示），不含扩展名
    /// </summary>
    public string DiskFileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件哈希值（SHA256），用于完整性校验和 ETag
    /// </summary>
    public string? FileHash { get; set; }

    /// <summary>
    /// 分块加密时每块明文大小（字节），默认 1MB
    /// </summary>
    public int BlockSize { get; set; } = 1048576;

    // ===== 公共访问相关字段 =====

    /// <summary>
    /// 是否允许匿名公共访问
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// 公共访问路径（如 "/public/documents/report.pdf"）
    /// </summary>
    public string? PublicPath { get; set; }

    // ===== WS 存储相关字段 =====

    /// <summary>
    /// 存储模式：Local（本地存储）/ WebSocket（WS客户端转发）/ Hybrid（混合）
    /// </summary>
    public string StorageMode { get; set; } = "Local";

    /// <summary>
    /// 关联的WS客户端ID（当 StorageMode 为 WebSocket 时有效）
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// 在存储端（本地或WS客户端）的实际存储路径
    /// </summary>
    public string? StoragePath { get; set; }
}
