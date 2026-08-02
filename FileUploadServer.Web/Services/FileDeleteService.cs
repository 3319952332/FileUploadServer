using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Services;

/// <summary>
/// 文件删除共享服务。
/// <para>统一清理文件的物理存储：WS 远程节点文件 + FileLocation 记录 + 本地磁盘文件（含加密子目录），
/// 供 API 删除（FileApiController.Delete）与网页删除（Index.cshtml.cs）共用，
/// 杜绝删除逻辑分叉导致 WS 节点密文残留。</para>
/// <para>注意：本服务只负责物理存储清理，Files 表记录的删除由调用方负责。</para>
/// </summary>
public class FileDeleteService
{
    private readonly IWebHostEnvironment _env;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileDeleteService> _logger;

    public FileDeleteService(
        IWebHostEnvironment env,
        IServiceScopeFactory scopeFactory,
        ILogger<FileDeleteService> logger)
    {
        _env = env;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 清理文件的所有物理存储：
    /// <list type="number">
    /// <item>WS 存储时：删除远程节点文件 + 对应的 FileLocation 记录（节点离线时记录仍删除）</item>
    /// <item>删除本地磁盘文件（加密文件用子目录 + DiskFileName，明文用 StoredFileName）</item>
    /// </list>
    /// </summary>
    /// <param name="file">待删除的文件元数据</param>
    public async Task DeletePhysicalAsync(FileItem file)
    {
        // 1. WS 存储：删除远程节点文件 + FileLocation 记录
        if (file.StorageMode == "WebSocket" && !string.IsNullOrEmpty(file.ClientId))
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wsStrategy = scope.ServiceProvider.GetRequiredService<WsStorageStrategy>();
            var remotePath = file.StoragePath ?? file.FileName;

            try
            {
                await wsStrategy.DeleteAsync(remotePath);
                _logger.LogInformation("已删除 WS 客户端文件: {Path} (ClientId: {ClientId})", remotePath, file.ClientId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WS 删除失败: ClientId={ClientId}, Path={Path}（节点可能离线，记录仍删除）",
                    file.ClientId, remotePath);
            }

            try
            {
                var fileLocations = await dbContext.Set<FileLocation>()
                    .Where(fl => fl.FilePath == remotePath && fl.ClientId == file.ClientId)
                    .ToListAsync();
                if (fileLocations.Count > 0)
                {
                    dbContext.Set<FileLocation>().RemoveRange(fileLocations);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation("已删除 {Count} 条 FileLocation 记录: {Path}", fileLocations.Count, remotePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除 FileLocation 记录失败: Path={Path}", remotePath);
            }
        }

        // 2. 本地磁盘文件（加密文件用子目录 + DiskFileName，明文用 StoredFileName）
        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        var filePath = FileDownloadService.ResolveDiskPath(uploadsPath, file);
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
            _logger.LogInformation("已删除本地物理文件: {FilePath}", filePath);
        }
    }
}
