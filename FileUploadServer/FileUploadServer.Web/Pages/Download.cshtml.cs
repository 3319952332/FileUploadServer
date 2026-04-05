using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FileUploadServer.Web.Pages;

public class DownloadModel : PageModel
{
    private readonly IFileItemRepository _repository;
    private readonly IWebHostEnvironment _env;

    public DownloadModel(IFileItemRepository repository, IWebHostEnvironment env)
    {
        _repository = repository;
        _env = env;
    }

    public async Task<IActionResult> OnGetAsync(int id, bool? preview)
    {
        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        var filePath = Path.Combine(uploadsPath, file.StoredFileName);

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
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
