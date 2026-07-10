using System.Security.Cryptography;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileApiController> _logger;

    public FileApiController(
        IFileItemRepository repository,
        IWebHostEnvironment env,
        IPermissionService permissionService,
        AppDbContext dbContext,
        IServiceScopeFactory scopeFactory,
        ILogger<FileApiController> logger)
    {
        _repository = repository;
        _env = env;
        _permissionService = permissionService;
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _logger = logger;
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
    /// 上传文件（自动关联当前密钥，支持透明加密）
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

        // 尝试获取加密服务
        IKeyProvider? keyProvider = null;
        try
        {
            keyProvider = _scopeFactory.CreateScope().ServiceProvider.GetService<IKeyProvider>();
        }
        catch
        {
            // 加密服务不可用，使用明文存储
        }

        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        if (!Directory.Exists(uploadsPath))
        {
            Directory.CreateDirectory(uploadsPath);
        }

        var storedFileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
        var diskFileName = storedFileName; // 默认与 StoredFileName 相同
        var encryptionVersion = (ushort)0;
        var keyVersion = (ushort)0;
        var blockSize = 1048576;

        var filePath = Path.Combine(uploadsPath, storedFileName);

        if (keyProvider != null)
        {
            // 加密已启用：使用加密流写入
            encryptionVersion = 1;
            keyVersion = keyProvider.CurrentKeyVersion;

            // 生成随机磁盘文件名（SHA256 哈希前32字符作为hex）
            var hashInput = $"{Guid.NewGuid()}{keyProvider.GetMasterKey()[..8]:X}";
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
            diskFileName = Convert.ToHexString(hash)[..32].ToLowerInvariant();

            // 创建子目录（前2字符）
            var subDir = Path.Combine(uploadsPath, diskFileName[..2]);
            if (!Directory.Exists(subDir))
            {
                Directory.CreateDirectory(subDir);
            }
            filePath = Path.Combine(subDir, diskFileName);

            var masterKey = keyProvider.GetMasterKey(keyVersion);
            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var encryptStream = new AesGcmEncryptStream(fileStream, masterKey, keyVersion, blockSize))
            {
                await file.CopyToAsync(encryptStream);
                encryptStream.Flush(); // 同步刷新，确保文件头和数据块写入
                fileStream.Flush(true); // 强制刷盘
            }

            var actualSize = new FileInfo(filePath).Length;
            _logger.LogInformation("文件已加密存储: {DiskFileName} (KeyVer={KeyVer}, BlockSize={BlockSize}, DiskSize={DiskSize})",
                diskFileName, keyVersion, blockSize, actualSize);
        }
        else
        {
            // 加密未启用：明文写入
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
        }

        var fileItem = new FileItem
        {
            FileName = file.FileName,
            StoredFileName = storedFileName,
            DiskFileName = diskFileName,
            FileSize = file.Length,
            ContentType = file.ContentType ?? "application/octet-stream",
            UploadedAt = DateTime.UtcNow,
            ApiKeyId = currentKey.Id,
            EncryptionVersion = encryptionVersion,
            KeyVersion = keyVersion,
            BlockSize = blockSize,
            StorageMode = "Local"
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

        // 删除物理文件（支持加密文件的子目录路径）
        var uploadsPath = Path.Combine(_env.WebRootPath, "uploads");
        string filePath;
        if (file.EncryptionVersion > 0 && !string.IsNullOrEmpty(file.DiskFileName))
        {
            var subDir = Path.Combine(uploadsPath, file.DiskFileName[..2]);
            filePath = Path.Combine(subDir, file.DiskFileName);
        }
        else
        {
            filePath = Path.Combine(uploadsPath, file.StoredFileName);
        }
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
        }

        await _repository.DeleteAsync(file);
        await _repository.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// 下载文件（按权限检查，支持透明解密和流式传输）
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

        // 确定文件路径：加密文件使用 DiskFileName，未加密使用 StoredFileName
        string filePath;
        if (file.EncryptionVersion > 0 && !string.IsNullOrEmpty(file.DiskFileName))
        {
            // 加密文件：子目录 + DiskFileName
            var subDir = Path.Combine(uploadsPath, file.DiskFileName[..2]);
            filePath = Path.Combine(subDir, file.DiskFileName);
        }
        else
        {
            // 未加密文件：传统路径
            filePath = Path.Combine(uploadsPath, file.StoredFileName);
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        // 流式返回（不缓冲完整文件到内存）
        Stream fileStream;
        if (file.EncryptionVersion > 0)
        {
            // 尝试使用解密流
            try
            {
                var keyProvider = _scopeFactory.CreateScope().ServiceProvider.GetService<IKeyProvider>();
                if (keyProvider != null && keyProvider.SupportsKeyVersion(file.KeyVersion))
                {
                    var masterKey = keyProvider.GetMasterKey(file.KeyVersion);
                    var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fileStream = new AesGcmDecryptStream(fs, keyProvider);
                    _logger.LogInformation("流式解密下载: {FileName} (KeyVer={KeyVer})", file.FileName, file.KeyVersion);
                }
                else
                {
                    // 密钥不可用，尝试明文读取
                    fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }
            catch
            {
                // 解密失败，回退到明文
                fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }
        else
        {
            fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        return File(fileStream, file.ContentType, file.FileName);
    }
}
