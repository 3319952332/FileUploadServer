using System.Security.Cryptography;
using System.Text;
using FileUploadServer.Core.Entities;
using FileUploadServer.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FileUploadServer.Web.Services;

/// <summary>
/// WS 客户端认证服务。
/// 验证连接请求中的 clientId 和 token。
/// token = SHA256(clientId + ":" + clientSecret + ":" + timestamp)
/// </summary>
public class WsClientAuthService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<WsClientAuthService> _logger;

    /// <summary>时间戳允许的最大偏差（秒），默认 ±5 分钟。</summary>
    private const long TimestampToleranceSeconds = 300;

    public WsClientAuthService(AppDbContext dbContext, ILogger<WsClientAuthService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// 验证 WS 客户端的连接请求。
    /// </summary>
    /// <param name="clientId">客户端 ID。</param>
    /// <param name="token">认证令牌：SHA256(clientId + ":" + clientSecret + ":" + timestamp)。</param>
    /// <param name="timestamp">Unix 时间戳（秒）。</param>
    /// <returns>验证是否通过。</returns>
    public async Task<bool> ValidateConnectionAsync(string clientId, string token, long timestamp)
    {
        // 1. 验证时间戳新鲜度（±5 分钟）
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var diff = Math.Abs(now - timestamp);
        if (diff > TimestampToleranceSeconds)
        {
            _logger.LogWarning(
                "Auth timestamp out of range for client {ClientId}: timestamp={Timestamp}, now={Now}, diff={Diff}s",
                clientId, timestamp, now, diff);
            return false;
        }

        // 2. 查找客户端记录
        var client = await _dbContext.Set<WsClient>().FirstOrDefaultAsync(c => c.Id == clientId);
        if (client == null)
        {
            _logger.LogWarning("Auth failed: client {ClientId} not found", clientId);
            return false;
        }

        if (!client.IsEnabled)
        {
            _logger.LogWarning("Auth failed: client {ClientId} is disabled", clientId);
            return false;
        }

        // 3. 计算预期的 token
        //    token = SHA256(clientId + ":" + clientSecret + ":" + timestamp)
        //    服务端使用存储的 ClientSecretHash 作为密钥材料进行验证
        var expectedToken = ComputeToken(clientId, client.ClientSecretHash, timestamp);

        var isValid = string.Equals(token, expectedToken, StringComparison.OrdinalIgnoreCase);
        if (!isValid)
        {
            _logger.LogWarning("Auth failed: invalid token for client {ClientId}", clientId);
        }
        else
        {
            client.LastConnectedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Client {ClientId} authenticated successfully", clientId);
        }

        return isValid;
    }

    /// <summary>
    /// 计算认证 token。
    /// token = SHA256(clientId + ":" + secret + ":" + timestamp)
    /// </summary>
    public static string ComputeToken(string clientId, string secret, long timestamp)
    {
        var input = $"{clientId}:{secret}:{timestamp}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
