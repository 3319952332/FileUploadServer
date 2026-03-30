using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FileUploadServer.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IFileItemRepository _repository;
    private readonly ILogger<IndexModel> _logger;
    private readonly IWebHostEnvironment _env;

    public IndexModel(IFileItemRepository repository, ILogger<IndexModel> logger, IWebHostEnvironment env)
    {
        _repository = repository;
        _logger = logger;
        _env = env;
    }

    public List<FileItem> Files { get; set; } = new();

    [BindProperty]
    public IFormFile? UploadedFile { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? key { get; set; }

    public async Task OnGetAsync()
    {
        Files = await _repository.GetAllAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (UploadedFile == null || UploadedFile.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "请选择一个文件");
            Files = await _repository.GetAllAsync();
            return Page();
        }

        // 生成唯一存储文件名
        var storedFileName = Guid.NewGuid().ToString() + Path.GetExtension(UploadedFile.FileName);
        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");

        if (!Directory.Exists(uploadsPath))
        {
            Directory.CreateDirectory(uploadsPath);
        }

        var filePath = Path.Combine(uploadsPath, storedFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await UploadedFile.CopyToAsync(stream);
        }

        var fileItem = new FileItem
        {
            FileName = UploadedFile.FileName,
            StoredFileName = storedFileName,
            FileSize = UploadedFile.Length,
            ContentType = UploadedFile.ContentType ?? "application/octet-stream",
            UploadedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(fileItem);
        await _repository.SaveChangesAsync();

        _logger.LogInformation("文件上传成功: {FileName}, 大小: {Size} bytes", fileItem.FileName, fileItem.FileSize);

        if (!string.IsNullOrEmpty(key))
        {
            return RedirectToPage(new { key = key });
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
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
}
