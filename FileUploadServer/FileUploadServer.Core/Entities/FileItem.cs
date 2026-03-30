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
}
