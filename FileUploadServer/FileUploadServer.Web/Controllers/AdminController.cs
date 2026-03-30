using System.Net;
using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Controllers;

[ApiController]
[Route("api/admin/keys")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public AdminController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
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
    /// 创建新的临时API密钥（仅localhost）
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiKey>> CreateKey([FromQuery] string description = "", [FromQuery] int expireMinutes = 60)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var key = new ApiKey
        {
            Key = Guid.NewGuid().ToString("N"), // 生成随机密钥
            Description = description,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expireMinutes),
            IsDeleted = false
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
    /// 检查是否是localhost请求
    /// </summary>
    private bool IsLocalRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp != null && IPAddress.IsLoopback(remoteIp);
    }
}
