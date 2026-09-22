using System.Net;
using FileUploadServer.Core.Entities;
using FileUploadServer.Core.Models;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
            return StatusCode(StatusCodes.Status403Forbidden);
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
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // 验证keyType（Admin/Temporary/User 三种类型）
        if (keyType != "Admin" && keyType != "Temporary" && keyType != "User")
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
            return StatusCode(StatusCodes.Status403Forbidden);
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
    /// 续期/重新激活API密钥（仅localhost）
    /// - 将密钥重新激活（IsDeleted=false）并延长过期时间
    /// - 用于登录服务器为账号密钥(User)续期，或重新激活已过期的用户密钥
    /// </summary>
    [HttpPut("{key}/renew")]
    public async Task<ActionResult<ApiKey>> RenewKey(
        string key,
        [FromQuery] int expireMinutes = 1440)
    {
        if (!IsLocalRequest())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // 续期上限一年（防误用将密钥无限期延长）
        if (expireMinutes <= 0 || expireMinutes > 525600)
        {
            expireMinutes = 1440;
        }

        var apiKey = await _dbContext.ApiKeys.FirstOrDefaultAsync(k => k.Key == key);
        if (apiKey == null)
        {
            return NotFound();
        }

        // 重新激活：取消软删除 + 延长过期时间
        apiKey.IsDeleted = false;
        apiKey.ExpiresAt = DateTime.UtcNow.AddMinutes(expireMinutes);
        await _dbContext.SaveChangesAsync();

        return Ok(apiKey);
    }

    /// <summary>
    /// 清理所有已过期/已删除的密钥（仅localhost）
    /// </summary>
    [HttpDelete("cleanup")]
    public async Task<ActionResult<int>> CleanupExpired()
    {
        if (!IsLocalRequest())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // 用户密钥（User）不参与手动清理，与"用户 key 不自动删除"原则一致
        var expiredKeys = await _dbContext.ApiKeys
            .Where(k => k.KeyType != "User" && (k.IsDeleted || k.ExpiresAt < DateTime.UtcNow))
            .ToListAsync();

        _dbContext.ApiKeys.RemoveRange(expiredKeys);
        await _dbContext.SaveChangesAsync();

        return Ok(expiredKeys.Count);
    }

    /// <summary>
    /// 设置文件公共访问标记（公网可达的受控例外，供 MCP file_set_public 使用）。
    /// /api/admin/* 被鉴权中间件跳过且被 nginx ACL 整体屏蔽，本路由独立于 admin 前缀、
    /// 在网关单独放行：请求必须携带有效 Admin 类型 API Key（?key= 查询参数），localhost 免 key。
    /// publicPath 必须以配置的公共模式前缀（如 /public/）开头——URL 约定为 /p + publicPath，
    /// 不匹配模式的路径会被 PublicFileMiddleware 静默 404，故在写入前强制校验。
    /// </summary>
    [HttpPut("/api/file-public/{id}")]
    public async Task<IActionResult> SetFilePublic(
        int id,
        [FromBody] SetPublicRequest request,
        [FromQuery] string? key = null,
        [FromServices] IOptions<PublicPathOptions> publicPathOptions = null!)
    {
        if (!IsLocalRequest())
        {
            var apiKey = await ResolveAdminKeyAsync(key);
            if (apiKey == null)
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { status = "error", error_code = -32003, message = "权限不足：设置公开访问需要有效的 Admin 密钥" });
            }
        }

        var file = await _dbContext.Files.FindAsync(id);
        if (file == null)
        {
            return NotFound();
        }

        if (request.IsPublic)
        {
            var publicPath = request.PublicPath?.Trim();
            if (string.IsNullOrEmpty(publicPath))
            {
                return BadRequest(new { status = "error", message = "is_public=true 时必须提供 public_path" });
            }
            if (!publicPath.StartsWith('/'))
            {
                publicPath = "/" + publicPath;
            }
            if (!IsValidPublicPath(publicPath, publicPathOptions.Value.Patterns))
            {
                var prefixes = string.Join(", ", publicPathOptions.Value.Patterns.Select(PatternPrefix));
                return BadRequest(new
                {
                    status = "error",
                    message = $"public_path 必须匹配公共访问模式之一（{prefixes}），当前值: {publicPath}"
                });
            }

            // 公共路径唯一性检查（不同文件不能共用同一 publicPath）
            var conflict = await _dbContext.Files.FirstOrDefaultAsync(
                f => f.IsPublic && f.PublicPath == publicPath && f.Id != id);
            if (conflict != null)
            {
                return Conflict(new { status = "error", message = $"public_path 已被文件 {conflict.Id} 占用: {publicPath}" });
            }

            file.IsPublic = true;
            file.PublicPath = publicPath;
        }
        else
        {
            file.IsPublic = false;
            file.PublicPath = null;
        }

        await _dbContext.SaveChangesAsync();
        return Ok(new
        {
            file.Id,
            file.FileName,
            file.FileSize,
            file.ContentType,
            file.IsPublic,
            file.PublicPath,
            PublicUrl = file.IsPublic && !string.IsNullOrEmpty(file.PublicPath)
                ? $"/p{file.PublicPath}"
                : null,
        });
    }

    /// <summary>
    /// 按 key 查询参数解析有效的 Admin 密钥（供 admin 接口自校验用）。
    /// </summary>
    private async Task<ApiKey?> ResolveAdminKeyAsync(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        var apiKey = await _dbContext.ApiKeys.FirstOrDefaultAsync(k => k.Key == key && !k.IsDeleted);
        if (apiKey == null || !apiKey.IsValid() || apiKey.KeyType != "Admin")
        {
            return null;
        }
        return apiKey;
    }

    /// <summary>
    /// 校验 publicPath 是否落在配置的公共模式之下（取模式的目录前缀做大小写不敏感比较）。
    /// </summary>
    private static bool IsValidPublicPath(string publicPath, string[] patterns)
    {
        return patterns.Any(p => publicPath.StartsWith(PatternPrefix(p), StringComparison.OrdinalIgnoreCase));
    }

    private static string PatternPrefix(string pattern)
    {
        // "/public/*" -> "/public/"；无通配符则原样返回
        var idx = pattern.IndexOf('*');
        return idx >= 0 ? pattern[..idx] : pattern;
    }

    /// <summary>
    /// 查询所有公共文件（仅localhost，分页）
    /// </summary>
    /// <summary>公开文件列表（无需认证）</summary>
    [HttpGet("/api/public/files")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<object>>> GetPublicFileList()
    {
        var files = await _dbContext.Files
            .Where(f => f.IsPublic && !string.IsNullOrEmpty(f.PublicPath))
            .OrderByDescending(f => f.UploadedAt)
            .Select(f => new
            {
                f.Id,
                f.FileName,
                f.FileSize,
                f.ContentType,
                f.PublicPath,
                f.UploadedAt,
                Url = $"/p{f.PublicPath}"
            })
            .ToListAsync();
        return Ok(files);
    }

    [HttpGet("/api/admin/files/public")]
    public async Task<ActionResult<List<FileItem>>> GetPublicFiles(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!IsLocalRequest())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
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
            return StatusCode(StatusCodes.Status403Forbidden);
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
            return StatusCode(StatusCodes.Status403Forbidden);
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
            return StatusCode(StatusCodes.Status403Forbidden);
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
            return StatusCode(StatusCodes.Status403Forbidden);
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
