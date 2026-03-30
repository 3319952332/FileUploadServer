using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FileUploadServer.Web.Controllers;

[ApiController]
[Route("api/files")]
public class FileApiController : ControllerBase
{
    private readonly IFileItemRepository _repository;
    private readonly IWebHostEnvironment _env;

    public FileApiController(IFileItemRepository repository, IWebHostEnvironment env)
    {
        _repository = repository;
        _env = env;
    }

    /// <summary>
    /// 获取所有文件列表
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<FileItem>>> GetAll()
    {
        var files = await _repository.GetAllAsync();
        return Ok(files);
    }

    /// <summary>
    /// 获取单个文件信息
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FileItem>> GetById(int id)
    {
        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }
        return Ok(file);
    }

    /// <summary>
    /// 上传文件
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<FileItem>> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("文件不能为空");
        }

        // 生成唯一存储文件名
        var storedFileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");

        if (!Directory.Exists(uploadsPath))
        {
            Directory.CreateDirectory(uploadsPath);
        }

        var filePath = Path.Combine(uploadsPath, storedFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var fileItem = new FileItem
        {
            FileName = file.FileName,
            StoredFileName = storedFileName,
            FileSize = file.Length,
            ContentType = file.ContentType ?? "application/octet-stream",
            UploadedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(fileItem);
        await _repository.SaveChangesAsync();

        return Created($"api/files/{fileItem.Id}", fileItem);
    }

    /// <summary>
    /// 删除文件
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        // 删除物理文件
        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        var filePath = Path.Combine(uploadsPath, file.StoredFileName);
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
        }

        await _repository.DeleteAsync(file);
        await _repository.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// 下载文件
    /// </summary>
    [HttpGet("download/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(int id)
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
        return File(fileBytes, file.ContentType, file.FileName);
    }
}
