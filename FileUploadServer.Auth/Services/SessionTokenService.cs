using System.Security.Cryptography;
using System.Text;

namespace FileUploadServer.Auth.Services;

/// <summary>
/// 会话令牌：256-bit 随机十六进制令牌，库中仅存 SHA-256 哈希
/// </summary>
public static class SessionTokenService
{
    public static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
