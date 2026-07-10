using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Controllers;

/// <summary>
/// WS 客户端管理 API 控制器。
/// 所有操作仅限 localhost 访问。
/// </summary>
[ApiController]
[Route("api/admin/ws-clients")]
public class WsClientAdminController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly WsConnectionManager _connectionManager;
    private readonly ILogger<WsClientAdminController> _logger;

    public WsClientAdminController(
        AppDbContext dbContext,
        WsConnectionManager connectionManager,
        ILogger<WsClientAdminController> logger)
    {
        _dbContext = dbContext;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// 列出所有已注册的 WS 客户端。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var clients = await _dbContext.Set<WsClient>()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Description,
                c.IsEnabled,
                c.PathPrefixes,
                c.StorageCapacity,
                c.CurrentStorage,
                c.LastConnectedAt,
                c.CreatedAt,
                IsOnline = _connectionManager.GetConnection(c.Id) != null
            })
            .ToListAsync();

        return Ok(clients);
    }

    /// <summary>
    /// 注册新的 WS 客户端。
    /// 生成 clientId（自动）和 clientSecret，返回给调用方。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterWsClientRequest request)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(request.Description))
        {
            return BadRequest(new { message = "Description is required" });
        }

        // 生成客户端 ID
        var clientId = GenerateClientId(request.Description);

        // 生成客户端密钥
        var clientSecret = GenerateClientSecret();
        var clientSecretHash = ComputeHash(clientSecret);

        var client = new WsClient
        {
            Id = clientId,
            ClientSecretHash = clientSecretHash,
            Description = request.Description,
            IsEnabled = true,
            PathPrefixes = string.Join(",", request.PathPrefixes ?? Array.Empty<string>()),
            StorageCapacity = request.StorageCapacity > 0 ? request.StorageCapacity : -1,
            CurrentStorage = 0,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Set<WsClient>().Add(client);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Registered new WS client: {ClientId}, description: {Description}",
            clientId, request.Description);

        return Ok(new
        {
            id = clientId,
            clientSecret, // 仅创建时返回
            description = request.Description,
            pathPrefixes = request.PathPrefixes ?? Array.Empty<string>(),
            storageCapacity = client.StorageCapacity
        });
    }

    /// <summary>
    /// 注销 WS 客户端。
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Unregister(string id)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var client = await _dbContext.Set<WsClient>().FindAsync(id);
        if (client == null)
        {
            return NotFound(new { message = $"Client '{id}' not found" });
        }

        // 如果在线，先断开连接
        var connection = _connectionManager.GetConnection(id);
        if (connection != null)
        {
            await _connectionManager.UnregisterConnectionAsync(id);
        }

        _dbContext.Set<WsClient>().Remove(client);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Unregistered WS client: {ClientId}", id);

        return NoContent();
    }

    /// <summary>
    /// 查看 WS 客户端的状态和存储用量。
    /// </summary>
    [HttpGet("{id}/stats")]
    public async Task<IActionResult> GetStats(string id)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var client = await _dbContext.Set<WsClient>().FindAsync(id);
        if (client == null)
        {
            return NotFound(new { message = $"Client '{id}' not found" });
        }

        var connection = _connectionManager.GetConnection(id);
        var isOnline = connection != null;

        // 获取文件统计
        var fileCount = await _dbContext.Set<FileLocation>()
            .CountAsync(fl => fl.ClientId == id);

        var totalStorage = await _dbContext.Set<FileLocation>()
            .Where(fl => fl.ClientId == id)
            .SumAsync(fl => fl.FileSize);

        // 更新 CurrentStorage
        client.CurrentStorage = totalStorage;
        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            id = client.Id,
            description = client.Description,
            isEnabled = client.IsEnabled,
            isOnline,
            connectedAt = connection?.ConnectedAt,
            lastHeartbeat = connection?.LastHeartbeat,
            lastConnectedAt = client.LastConnectedAt,
            storage = new
            {
                capacity = client.StorageCapacity,
                used = totalStorage,
                available = client.StorageCapacity > 0
                    ? client.StorageCapacity - totalStorage
                    : -1,
                usagePercent = client.StorageCapacity > 0
                    ? Math.Round((double)totalStorage / client.StorageCapacity * 100, 2)
                    : 0
            },
            fileCount,
            pathPrefixes = client.PathPrefixes?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                           ?? Array.Empty<string>(),
            createdAt = client.CreatedAt
        });
    }

    /// <summary>
    /// 重新生成客户端密钥。
    /// </summary>
    [HttpPost("{id}/regenerate-secret")]
    public async Task<IActionResult> RegenerateSecret(string id)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var client = await _dbContext.Set<WsClient>().FindAsync(id);
        if (client == null)
        {
            return NotFound(new { message = $"Client '{id}' not found" });
        }

        var newSecret = GenerateClientSecret();
        client.ClientSecretHash = ComputeHash(newSecret);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Regenerated secret for WS client: {ClientId}", id);

        return Ok(new
        {
            id,
            clientSecret = newSecret // 仅此时返回
        });
    }

    /// <summary>
    /// 启用/禁用 WS 客户端。
    /// </summary>
    [HttpPatch("{id}/status")]
    public async Task<IActionResult> SetStatus(string id, [FromBody] SetClientStatusRequest request)
    {
        if (!IsLocalRequest())
        {
            return Forbid();
        }

        var client = await _dbContext.Set<WsClient>().FindAsync(id);
        if (client == null)
        {
            return NotFound(new { message = $"Client '{id}' not found" });
        }

        client.IsEnabled = request.IsEnabled;
        await _dbContext.SaveChangesAsync();

        if (!request.IsEnabled)
        {
            // 如果禁用，断开连接
            var connection = _connectionManager.GetConnection(id);
            if (connection != null)
            {
                await _connectionManager.UnregisterConnectionAsync(id);
            }
        }

        _logger.LogInformation("Client {ClientId} status changed to {Status}", id, request.IsEnabled ? "enabled" : "disabled");

        return Ok(new { id, isEnabled = client.IsEnabled });
    }

    /// <summary>
    /// 检查请求是否来自 localhost。
    /// </summary>
    private bool IsLocalRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp == null) return false;
        return IPAddress.IsLoopback(remoteIp);
    }

    /// <summary>
    /// 根据描述生成客户端 ID。
    /// </summary>
    private static string GenerateClientId(string description)
    {
        var prefix = new string(description.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return string.IsNullOrEmpty(prefix) ? $"client-{suffix}" : $"{prefix}-{suffix}";
    }

    /// <summary>
    /// 生成客户端密钥（32 字节随机 hex）。
    /// </summary>
    private static string GenerateClientSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "sk-wsc-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 计算 SHA256 哈希。
    /// </summary>
    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// 注册客户端的请求体。
/// </summary>
public class RegisterWsClientRequest
{
    /// <summary>客户端描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>支持的路径前缀列表。</summary>
    public string[]? PathPrefixes { get; set; }

    /// <summary>存储容量上限（字节），-1 表示无限制。</summary>
    public long StorageCapacity { get; set; } = -1;
}

/// <summary>
/// 设置客户端状态的请求体。
/// </summary>
public class SetClientStatusRequest
{
    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; }
}
