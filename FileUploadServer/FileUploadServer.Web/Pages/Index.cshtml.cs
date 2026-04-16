using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
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

    public IndexModel(
        IFileItemRepository repository,
        ILogger<IndexModel> logger,
        IWebHostEnvironment env,
        IPermissionService permissionService,
        AppDbContext dbContext)
    {
        _repository = repository;
        _logger = logger;
        _env = env;
        _permissionService = permissionService;
        _dbContext = dbContext;
    }

    public List<FileItem> Files { get; set; } = new();

    [BindProperty]
    public List<IFormFile>? UploadedFiles { get; set; }

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

        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");

        if (!Directory.Exists(uploadsPath))
        {
            Directory.CreateDirectory(uploadsPath);
        }

        foreach (var uploadedFile in UploadedFiles.Where(f => f.Length > 0))
        {
            // 生成唯一存储文件名
            var storedFileName = Guid.NewGuid().ToString() + Path.GetExtension(uploadedFile.FileName);
            var filePath = Path.Combine(uploadsPath, storedFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await uploadedFile.CopyToAsync(stream);
            }

            var fileItem = new FileItem
            {
                FileName = uploadedFile.FileName,
                StoredFileName = storedFileName,
                FileSize = uploadedFile.Length,
                ContentType = uploadedFile.ContentType ?? "application/octet-stream",
                UploadedAt = DateTime.UtcNow,
                ApiKeyId = _currentApiKey.Id
            };

            await _repository.AddAsync(fileItem);
        }

        await _repository.SaveChangesAsync();

        _logger.LogInformation("批量上传完成: {Count} 个文件上传成功", UploadedFiles.Count(f => f.Length > 0));

        if (!string.IsNullOrEmpty(key))
        {
            return RedirectToPage(new { key = key });
        }
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

        // Delete physical file
        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        var filePath = Path.Combine(uploadsPath, file.StoredFileName);
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
        }

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

        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        int deletedCount = 0;

        foreach (var id in ids)
        {
            var file = await _repository.GetByIdAsync(id);
            if (file != null && await _permissionService.CanAccessFileAsync(id, _currentApiKey))
            {
                // Delete physical file
                var filePath = Path.Combine(uploadsPath, file.StoredFileName);
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }

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
