using System.Security.Cryptography;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
using FileUploadServer.Web.Services;
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
    private readonly IStorageStrategyFactory _storageStrategyFactory;
    private readonly FileDeleteService _deleteService;
    private readonly ILogger<FileApiController> _logger;

    public FileApiController(
        IFileItemRepository repository,
        IWebHostEnvironment env,
        IPermissionService permissionService,
        AppDbContext dbContext,
        IServiceScopeFactory scopeFactory,
        IStorageStrategyFactory storageStrategyFactory,
        FileDeleteService deleteService,
        ILogger<FileApiController> logger)
    {
        _repository = repository;
        _env = env;
        _permissionService = permissionService;
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _storageStrategyFactory = storageStrategyFactory;
        _deleteService = deleteService;
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
    public async Task<ActionResult<FileItem>> Upload(IFormFile file, [FromForm] string? path = null)
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

        // 根据路径确定存储模式和客户端（确保路径以 / 开头）
        var uploadPath = string.IsNullOrEmpty(path) ? "/" + file.FileName : path;
        if (!uploadPath.StartsWith('/'))
            uploadPath = "/" + uploadPath;
        var strategy = _storageStrategyFactory.GetStrategy(uploadPath);
        var isWsStorage = strategy is WsStorageStrategy;
        string? clientId = null;

        if (isWsStorage)
        {
            var connectionManager = HttpContext.RequestServices.GetRequiredService<WsConnectionManager>();
            if (connectionManager.TryPickClientForPath(uploadPath, out var wsClient))
            {
                clientId = wsClient.ClientId;
            }
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
            StorageMode = (isWsStorage && clientId != null) ? "WebSocket" : "Local",
            ClientId = clientId,
        };

        // Forward file to WebSocket storage client
        if (isWsStorage && clientId != null)
        {
            try
            {
                using var readStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var memoryStream = new MemoryStream();
                await readStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var wsStrategy = HttpContext.RequestServices.GetRequiredService<WsStorageStrategy>();
                await wsStrategy.WriteAsync(uploadPath, memoryStream);

                // Create FileLocation record for WS storage
                var fileLocation = new FileLocation
                {
                    Id = Guid.NewGuid(),
                    FilePath = uploadPath,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    ClientId = clientId,
                    ApiKeyId = currentKey.Id,
                    IsPublic = false,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Set<FileLocation>().Add(fileLocation);

                fileItem.StoragePath = uploadPath;
                _logger.LogInformation("File forwarded to WS client {ClientId}: {Path}", clientId, uploadPath);

                // WS 转发成功后删除本地临时副本（本地仅作中转，正式存储为 WS 节点），避免本地残留累积
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                    _logger.LogInformation("已删除上传本地临时副本: {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WS write failed for {Path}, file saved locally only", uploadPath);
                fileItem.StorageMode = "Local";
                fileItem.ClientId = null;
            }
        }

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

        // 统一清理物理文件（WS 远程 + FileLocation + 本地加密子目录）
        await _deleteService.DeletePhysicalAsync(file);

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

        // 统一走共享下载服务（WS 存储 / 本地存储 + 透明解密）
        var downloadService = HttpContext.RequestServices.GetRequiredService<FileDownloadService>();
        Stream stream;
        try
        {
            stream = await downloadService.OpenDecryptedStreamAsync(file);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No available WS client"))
        {
            _logger.LogWarning("WS client offline for download: ClientId={ClientId}, Path={Path}", file.ClientId, file.StoragePath);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Storage client is currently offline");
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed: Id={Id}, ClientId={ClientId}", id, file.ClientId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Failed to retrieve file from storage client");
        }

        return File(stream, file.ContentType, file.FileName);
    }
}
