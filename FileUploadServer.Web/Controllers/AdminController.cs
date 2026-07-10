using System.Net;
using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Controllers;

[ApiController]
[Route("api/admin/keys")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IIpWhitelistService _ipWhitelistService;

    public AdminController(AppDbContext dbContext, IIpWhitelistService ipWhitelistService)
    {
        _dbContext = dbContext;
        _ipWhitelistService = ipWhitelistService;
    }

    /// <summary>
    /// 列出所有API密钥（仅localhost）
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ApiKey>>> ListKeys()
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var keys = await _dbContext.ApiKeys
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        return Ok(keys);
    }

    /// <summary>
    /// 创建新的API密钥（仅localhost）
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiKey>> CreateKey(
        [FromQuery] string description = "",
        [FromQuery] int expireMinutes = 1440,
        [FromQuery] string keyType = "Admin")
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        // 验证keyType
        if (keyType != "Admin" && keyType != "Temporary")
        {
            keyType = "Admin";
        }

        var key = new ApiKey
        {
            Key = Guid.NewGuid().ToString("N"),
            Description = description,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expireMinutes),
            IsDeleted = false,
            KeyType = keyType
        };

        _dbContext.ApiKeys.Add(key);
        await _dbContext.SaveChangesAsync();

        return Created($"api/admin/keys/{key.Id}", key);
    }

    /// <summary>
    /// 删除（禁用）一个API密钥（仅localhost）
    /// </summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> DeleteKey(string key)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var apiKey = await _dbContext.ApiKeys.FirstOrDefaultAsync(k => k.Key == key);
        if (apiKey == null)
        {
            return NotFound();
        }

        apiKey.IsDeleted = true;
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// 清理所有已过期/已删除的密钥（仅localhost）
    /// </summary>
    [HttpDelete("cleanup")]
    public async Task<ActionResult<int>> CleanupExpired()
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var expiredKeys = await _dbContext.ApiKeys
            .Where(k => k.IsDeleted || k.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();

        _dbContext.ApiKeys.RemoveRange(expiredKeys);
        await _dbContext.SaveChangesAsync();

        return Ok(expiredKeys.Count);
    }

    /// <summary>
    /// 设置文件公共访问标记（仅localhost）
    /// </summary>
    [HttpPut("/api/admin/files/{id}/public")]
    public async Task<IActionResult> SetFilePublic(int id, [FromBody] SetPublicRequest request)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var file = await _dbContext.Files.FindAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        file.IsPublic = request.IsPublic;
        file.PublicPath = request.IsPublic ? request.PublicPath : null;

        await _dbContext.SaveChangesAsync();
        return Ok(file);
    }

    /// <summary>
    /// 查询所有公共文件（仅localhost，分页）
    /// </summary>
    [HttpGet("/api/admin/files/public")]
    public async Task<ActionResult<List<FileItem>>> GetPublicFiles(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var files = await _dbContext.Files
            .Where(f => f.IsPublic)
            .OrderByDescending(f => f.UploadedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(files);
    }

    /// <summary>
    /// 公共文件访问统计（仅localhost）
    /// </summary>
    [HttpGet("/api/admin/stats/public-access")]
    public async Task<ActionResult<object>> GetPublicAccessStats()
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var publicFiles = await _dbContext.Files
            .Where(f => f.IsPublic)
            .ToListAsync();

        return Ok(new
        {
            totalCount = publicFiles.Count,
            totalSize = publicFiles.Sum(f => f.FileSize),
            files = publicFiles.Select(f => new
            {
                f.Id,
                f.FileName,
                f.PublicPath,
                f.FileSize,
                f.UploadedAt
            })
        });
    }

    /// <summary>
    /// 检查是否是localhost请求
    /// </summary>
    private bool IsLocalRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp != null && IPAddress.IsLoopback(remoteIp);
    }
}

/// <summary>
/// IP白名单管理控制器（仅localhost）
/// </summary>
[ApiController]
[Route("api/admin/whitelist")]
public class IpWhitelistController : ControllerBase
{
    private readonly IIpWhitelistService _ipWhitelistService;

    public IpWhitelistController(IIpWhitelistService ipWhitelistService)
    {
        _ipWhitelistService = ipWhitelistService;
    }

    private bool IsLocalRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp != null && IPAddress.IsLoopback(remoteIp);
    }

    /// <summary>
    /// 列出所有IP白名单（仅localhost）
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<IpWhitelist>>> ListWhitelist()
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var whitelist = await _ipWhitelistService.GetAllAsync();
        return Ok(whitelist);
    }

    /// <summary>
    /// 添加IP到白名单（仅localhost）
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<IpWhitelist>> AddToWhitelist(
        [FromQuery] string ipAddress,
        [FromQuery] string description = "")
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(ipAddress))
        {
            return BadRequest("IP地址不能为空");
        }

        await _ipWhitelistService.AddAsync(ipAddress, description);

        // 获取刚添加的IP并返回
        var whitelist = await _ipWhitelistService.GetAllAsync();
        var added = whitelist.FirstOrDefault(w => w.IpAddress == ipAddress);

        if (added == null)
        {
            return StatusCode(500, "添加失败");
        }

        return Created($"api/admin/whitelist/{added.Id}", added);
    }

    /// <summary>
    /// 从白名单移除IP（仅localhost）
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> RemoveFromWhitelist(int id)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        await _ipWhitelistService.RemoveAsync(id);
        return NoContent();
    }
}

/// <summary>
/// 公网API控制器 - 用于公开申请临时密钥
/// </summary>
[ApiController]
[Route("api/public/keys")]
public class PublicKeysController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IIpWhitelistService _ipWhitelistService;

    public PublicKeysController(AppDbContext dbContext, IIpWhitelistService ipWhitelistService)
    {
        _dbContext = dbContext;
        _ipWhitelistService = ipWhitelistService;
    }

    /// <summary>
    /// 申请临时密钥（公网可访问，需要IP在白名单中）
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiKey>> CreateTemporaryKey(
        [FromQuery] string description = "",
        [FromQuery] int expireMinutes = 60)
    {
        // 获取客户端IP
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        
        // 检查IP是否在白名单中
        if (string.IsNullOrEmpty(remoteIp) || !await _ipWhitelistService.IsIpAllowedAsync(remoteIp))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "IP地址不在白名单中");
        }

        // 限制临时密钥最大过期时间为24小时
        if (expireMinutes <= 0 || expireMinutes > 1440)
        {
            expireMinutes = 60;
        }

        var key = new ApiKey
        {
            Key = Guid.NewGuid().ToString("N"),
            Description = description,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expireMinutes),
            IsDeleted = false,
            KeyType = "Temporary"
        };

        _dbContext.ApiKeys.Add(key);
        await _dbContext.SaveChangesAsync();

        return Created($"api/public/keys/{key.Id}", key);
    }
}

/// <summary>
/// 设置公共访问请求体
/// </summary>
public class SetPublicRequest
{
    public bool IsPublic { get; set; }
    public string? PublicPath { get; set; }
}
