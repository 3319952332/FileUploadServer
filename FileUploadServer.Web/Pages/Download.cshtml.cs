using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Pages;

public class DownloadModel : PageModel
{
    private readonly IFileItemRepository _repository;
    private readonly IWebHostEnvironment _env;
    private readonly IPermissionService _permissionService;
    private readonly AppDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public DownloadModel(
        IFileItemRepository repository,
        IWebHostEnvironment env,
        IPermissionService permissionService,
        AppDbContext dbContext,
        IServiceScopeFactory scopeFactory)
    {
        _repository = repository;
        _env = env;
        _permissionService = permissionService;
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    [BindProperty(SupportsGet = true)]
    public string? key { get; set; }

    private async Task<ApiKey?> GetCurrentApiKeyAsync()
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return await _dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Key == key && !k.IsDeleted && k.ExpiresAt > DateTime.UtcNow);
    }

    public async Task<IActionResult> OnGetAsync(int id, bool? preview)
    {
        var currentApiKey = await GetCurrentApiKeyAsync();

        if (currentApiKey == null)
        {
            return Unauthorized();
        }

        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        if (!await _permissionService.CanAccessFileAsync(id, currentApiKey))
        {
            return Forbid();
        }

        byte[] fileBytes;
        Stream sourceStream;
        using var diScope = _scopeFactory.CreateScope();
        var services = diScope.ServiceProvider;

        // 统一走共享下载服务（WS 存储 / 本地存储 + 透明解密）
        var downloadService = services.GetRequiredService<FileDownloadService>();
        try
        {
            sourceStream = await downloadService.OpenDecryptedStreamAsync(file);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(503, $"Storage node unavailable: {ex.Message}");
        }

        // 解密流已由共享服务按需包装，读入内存后返回
        try
        {
            using var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }
        finally
        {
            sourceStream.Dispose();
        }
        if (preview == true)
        {
            // 预览模式：inline 直接在浏览器打开，不下载
            return File(fileBytes, file.ContentType);
        }
        else
        {
            // 下载模式：attachment 强制下载
            return File(fileBytes, file.ContentType, file.FileName);
        }
    }
}
