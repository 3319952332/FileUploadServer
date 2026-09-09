using System.Text.Json;

namespace FileUploadServer.Auth.Services;

/// <summary>
/// 文件服本地 admin 通道客户端（部署在同一台服务器，走 localhost 直连）
/// 职责：向文件服申请/续期/删除 User 类型账号密钥 —— 即"向文件服申请 key，下发给应用"。
/// </summary>
public class FileServerAdminClient
{
    private readonly HttpClient _http;
    private readonly ILogger<FileServerAdminClient> _logger;

    public FileServerAdminClient(HttpClient http, ILogger<FileServerAdminClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>在文件服创建账号密钥（User/Admin），返回 (key, keyId)</summary>
    public async Task<(string Key, int KeyId)> CreateUserKeyAsync(string username, string keyType, int expireMinutes)
    {
        var url = $"{BaseUrl}/api/admin/keys?keyType={Uri.EscapeDataString(keyType)}&expireMinutes={expireMinutes}&description={Uri.EscapeDataString("account:" + username)}";
        using var resp = await _http.PostAsync(url, null);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var key = root.GetProperty("key").GetString()!;
        var id = root.GetProperty("id").GetInt32();
        _logger.LogInformation("文件服创建 {Type} 密钥 fileKeyId={KeyId} (account:{Username})", keyType, id, username);
        return (key, id);
    }

    /// <summary>续期/重新激活密钥（过期也可重新激活）</summary>
    public async Task RenewKeyAsync(string key, int expireMinutes)
    {
        var url = $"{BaseUrl}/api/admin/keys/{Uri.EscapeDataString(key)}/renew?expireMinutes={expireMinutes}";
        using var resp = await _http.PutAsync(url, null);
        resp.EnsureSuccessStatusCode();
        _logger.LogInformation("文件服续期密钥 {KeyPrefix}... 至 {ExpireMinutes} 分钟", key[..Math.Min(8, key.Length)], expireMinutes);
    }

    /// <summary>软删除密钥（删除账号时回收）</summary>
    public async Task DeleteKeyAsync(string key)
    {
        var url = $"{BaseUrl}/api/admin/keys/{Uri.EscapeDataString(key)}";
        using var resp = await _http.DeleteAsync(url);
        resp.EnsureSuccessStatusCode();
    }

    public string BaseUrl { get; init; } = "http://localhost:7000";
}
