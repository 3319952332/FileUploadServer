using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Controllers;

[ApiController]
[Route("api/files")]
public class FileApiController : ControllerBase
{
    private readonly IFileItemRepository _repository;
    private readonly IWebHostEnvironment _env;
    private readonly IPermissionService _permissionService;
    private readonly AppDbContext _dbContext;

    public FileApiController(
        IFileItemRepository repository,
        IWebHostEnvironment env,
        IPermissionService permissionService,
        AppDbContext dbContext)
    {
        _repository = repository;
        _env = env;
        _permissionService = permissionService;
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取当前API密钥
    /// </summary>
    private ApiKey? GetCurrentApiKey()
    {
        return HttpContext.Items["CurrentApiKey"] as ApiKey;
    }

    /// <summary>
    /// 获取所有文件列表（按权限过滤）
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<FileItem>>> GetAll()
    {
        var currentKey = GetCurrentApiKey();
        if (currentKey == null)
        {
            return Unauthorized();
        }

        var allFilesQuery = _repository.GetQueryable().OrderByDescending(f => f.UploadedAt);
        var accessibleFiles = _permissionService.GetAccessibleFilesQuery(currentKey, allFilesQuery);
        var files = await accessibleFiles.ToListAsync();

        return Ok(files);
    }

    /// <summary>
    /// 获取单个文件信息（按权限检查）
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<FileItem>> GetById(int id)
    {
        var currentKey = GetCurrentApiKey();
        if (currentKey == null)
        {
            return Unauthorized();
        }

        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        if (!await _permissionService.CanAccessFileAsync(id, currentKey))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(file);
    }

    /// <summary>
    /// 上传文件（自动关联当前密钥）
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<FileItem>> Upload(IFormFile file)
    {
        var currentKey = GetCurrentApiKey();
        if (currentKey == null)
        {
            return Unauthorized();
        }

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
            UploadedAt = DateTime.UtcNow,
            ApiKeyId = currentKey.Id
        };

        await _repository.AddAsync(fileItem);
        await _repository.SaveChangesAsync();

        return Created($"api/files/{fileItem.Id}", fileItem);
    }

    /// <summary>
    /// 删除文件（按权限检查）
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(int id)
    {
        var currentKey = GetCurrentApiKey();
        if (currentKey == null)
        {
            return Unauthorized();
        }

        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        if (!await _permissionService.CanAccessFileAsync(id, currentKey))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
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
    /// 下载文件（按权限检查）
    /// </summary>
    [HttpGet("download/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Download(int id)
    {
        var currentKey = GetCurrentApiKey();
        if (currentKey == null)
        {
            return Unauthorized();
        }

        var file = await _repository.GetByIdAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        if (!await _permissionService.CanAccessFileAsync(id, currentKey))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
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
