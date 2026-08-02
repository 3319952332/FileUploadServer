using System.Security.Cryptography;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
using FileUploadServer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IFileItemRepository _repository;
    private readonly ILogger<IndexModel> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IPermissionService _permissionService;
    private readonly AppDbContext _dbContext;
    private readonly IStorageStrategyFactory _storageStrategyFactory;
    private readonly IServiceScopeFactory _scopeFactory;

    public IndexModel(
        IFileItemRepository repository,
        ILogger<IndexModel> logger,
        IWebHostEnvironment env,
        IPermissionService permissionService,
        AppDbContext dbContext,
        IStorageStrategyFactory storageStrategyFactory,
        IServiceScopeFactory scopeFactory)
    {
        _repository = repository;
        _logger = logger;
        _env = env;
        _permissionService = permissionService;
        _dbContext = dbContext;
        _storageStrategyFactory = storageStrategyFactory;
        _scopeFactory = scopeFactory;
    }

    public List<FileItem> Files { get; set; } = new();

    [BindProperty]
    public List<IFormFile>? UploadedFiles { get; set; }

    [BindProperty]
    public string? path { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? key { get; set; }

    private ApiKey? _currentApiKey;

    private async Task<ApiKey?> GetCurrentApiKeyAsync()
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return await _dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Key == key && !k.IsDeleted && k.ExpiresAt > DateTime.UtcNow);
    }

    public async Task OnGetAsync()
    {
        _currentApiKey = await GetCurrentApiKeyAsync();

        if (_currentApiKey == null)
        {
            Files = new List<FileItem>();
            return;
        }

        var allFilesQuery = _repository.GetQueryable().OrderByDescending(f => f.UploadedAt);
        var accessibleFiles = _permissionService.GetAccessibleFilesQuery(_currentApiKey, allFilesQuery);
        Files = await accessibleFiles.ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        _currentApiKey = await GetCurrentApiKeyAsync();

        if (_currentApiKey == null)
        {
            ModelState.AddModelError(string.Empty, "无效的API密钥");
            Files = new List<FileItem>();
            return Page();
        }

        if (UploadedFiles == null || UploadedFiles.Count == 0 || UploadedFiles.All(f => f.Length == 0))
        {
            ModelState.AddModelError(string.Empty, "请选择至少一个文件");
            var allFilesQuery = _repository.GetQueryable().OrderByDescending(f => f.UploadedAt);
            var accessibleFiles = _permissionService.GetAccessibleFilesQuery(_currentApiKey, allFilesQuery);
            Files = await accessibleFiles.ToListAsync();
            return Page();
        }

        using var diScope = _scopeFactory.CreateScope();
        var services = diScope.ServiceProvider;

        IKeyProvider? keyProvider = null;
        try { keyProvider = services.GetService<IKeyProvider>(); }
        catch { }

        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        if (!Directory.Exists(uploadsPath))
            Directory.CreateDirectory(uploadsPath);

        int successCount = 0;

        foreach (var uploadedFile in UploadedFiles.Where(f => f.Length > 0))
        {
            // 路由
            var uploadPath = string.IsNullOrEmpty(path) ? "/" + uploadedFile.FileName : path;
            if (!uploadPath.StartsWith('/')) uploadPath = "/" + uploadPath;
            var strategy = _storageStrategyFactory.GetStrategy(uploadPath);
            var isWsStorage = strategy is WsStorageStrategy;
            string? clientId = null;
            if (isWsStorage)
            {
                var cm = HttpContext.RequestServices.GetRequiredService<WsConnectionManager>();
                if (cm.TryPickClientForPath(uploadPath, out var wsClient))
                    clientId = wsClient.ClientId;
            }

            // 加密
            var storedFileName = Guid.NewGuid().ToString() + Path.GetExtension(uploadedFile.FileName);
            var diskFileName = storedFileName;
            var encryptionVersion = (ushort)0;
            var keyVersion = (ushort)0;
            var blockSize = 1048576;
            var localFilePath = Path.Combine(uploadsPath, storedFileName);

            if (keyProvider != null)
            {
                encryptionVersion = 1;
                keyVersion = keyProvider.CurrentKeyVersion;
                var hashInput = $"{Guid.NewGuid()}{keyProvider.GetMasterKey()[..8]:X}";
                var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
                diskFileName = Convert.ToHexString(hash)[..32].ToLowerInvariant();
                var subDir = Path.Combine(uploadsPath, diskFileName[..2]);
                if (!Directory.Exists(subDir)) Directory.CreateDirectory(subDir);
                localFilePath = Path.Combine(subDir, diskFileName);

                var masterKey = keyProvider.GetMasterKey(keyVersion);
                using var fs = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                using var es = new AesGcmEncryptStream(fs, masterKey, keyVersion, blockSize);
                await uploadedFile.CopyToAsync(es);
                es.Flush(); fs.Flush(true);
            }
            else
            {
                using var fs = new FileStream(localFilePath, FileMode.Create);
                await uploadedFile.CopyToAsync(fs);
            }

            var fileItem = new FileItem
            {
                FileName = uploadedFile.FileName,
                StoredFileName = storedFileName,
                DiskFileName = diskFileName,
                FileSize = uploadedFile.Length,
                ContentType = uploadedFile.ContentType ?? "application/octet-stream",
                UploadedAt = DateTime.UtcNow,
                ApiKeyId = _currentApiKey.Id,
                EncryptionVersion = encryptionVersion,
                KeyVersion = keyVersion,
                BlockSize = blockSize,
                StorageMode = (isWsStorage && clientId != null) ? "WebSocket" : "Local",
                ClientId = clientId,
            };

            // WS转发
            if (isWsStorage && clientId != null)
            {
                try
                {
                    using var readStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var ms = new MemoryStream();
                    await readStream.CopyToAsync(ms);
                    ms.Position = 0;
                    var wsStrategy = services.GetRequiredService<WsStorageStrategy>();
                    await wsStrategy.WriteAsync(uploadPath, ms);
                    fileItem.StoragePath = uploadPath;
                    _logger.LogInformation("Web上传转发到WS: {Path}", uploadPath);

                    // WS 转发成功后删除本地临时副本（本地仅作中转，正式存储为 WS 节点），避免本地残留累积
                    if (System.IO.File.Exists(localFilePath))
                    {
                        System.IO.File.Delete(localFilePath);
                        _logger.LogInformation("已删除上传本地临时副本: {FilePath}", localFilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Web上传WS转发失败: {Path}", uploadPath);
                    fileItem.StorageMode = "Local";
                    fileItem.ClientId = null;
                }
            }

            await _repository.AddAsync(fileItem);
            successCount++;
        }

        await _repository.SaveChangesAsync();
        _logger.LogInformation("Web批量上传完成: {Count} 个文件", successCount);

        if (!string.IsNullOrEmpty(key))
            return RedirectToPage(new { key = key });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        _currentApiKey = await GetCurrentApiKeyAsync();

        if (_currentApiKey == null)
        {
            return NotFound();
        }

        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        if (!await _permissionService.CanAccessFileAsync(id, _currentApiKey))
        {
            return Forbid();
        }

        // 清理物理文件（WS 远程 + FileLocation + 本地加密子目录）
        using var deleteScope = _scopeFactory.CreateScope();
        var deleteService = deleteScope.ServiceProvider.GetRequiredService<FileDeleteService>();
        await deleteService.DeletePhysicalAsync(file);

        await _repository.DeleteAsync(file);
        await _repository.SaveChangesAsync();

        if (!string.IsNullOrEmpty(key))
        {
            return RedirectToPage(new { key = key });
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBatchDeleteAsync([FromForm] int[] ids)
    {
        _currentApiKey = await GetCurrentApiKeyAsync();

        if (_currentApiKey == null)
        {
            ModelState.AddModelError(string.Empty, "无效的API密钥");
            Files = new List<FileItem>();
            return Page();
        }

        if (ids == null || ids.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择至少一个文件");
            var allFilesQuery = _repository.GetQueryable().OrderByDescending(f => f.UploadedAt);
            var accessibleFiles = _permissionService.GetAccessibleFilesQuery(_currentApiKey, allFilesQuery);
            Files = await accessibleFiles.ToListAsync();
            return Page();
        }

        using var deleteScope = _scopeFactory.CreateScope();
        var deleteService = deleteScope.ServiceProvider.GetRequiredService<FileDeleteService>();
        int deletedCount = 0;

        foreach (var id in ids)
        {
            var file = await _repository.GetByIdAsync(id);
            if (file != null && await _permissionService.CanAccessFileAsync(id, _currentApiKey))
            {
                // 清理物理文件（WS 远程 + FileLocation + 本地加密子目录）
                await deleteService.DeletePhysicalAsync(file);

                await _repository.DeleteAsync(file);
                deletedCount++;
            }
        }

        await _repository.SaveChangesAsync();

        _logger.LogInformation("批量删除完成: {Count} 个文件已删除", deletedCount);

        if (!string.IsNullOrEmpty(key))
        {
            return RedirectToPage(new { key = key });
        }
        return RedirectToPage();
    }
}
