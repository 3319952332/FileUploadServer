using System.Security.Cryptography;
using System.Text;
using FileUploadServer.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileUploadServer.Infrastructure.Encryption;

/// <summary>
/// 密钥提供者实现
/// 密钥加载优先级：
///   1. 环境变量 FILE_ENCRYPTION_KEY（base64 编码，32 字节）
///   2. 密钥文件（默认路径 /etc/fileuploadserver/encryption.key 或配置指定）
///   3. 配置项 Encryption:MasterKey（仅开发环境）
///   4. 首次启动自动生成密钥文件
/// </summary>
public class KeyProvider : IKeyProvider
{
    private readonly byte[] _masterKey;
    private readonly ushort _currentKeyVersion;
    private readonly Dictionary<ushort, byte[]> _historicalKeys = new();
    private readonly ILogger<KeyProvider> _logger;
    private readonly string _keyFilePath;

    /// <summary>
    /// 默认密钥文件路径
    /// </summary>
    public const string DefaultKeyFilePath = "/etc/fileuploadserver/encryption.key";

    /// <summary>
    /// 初始化密钥提供者
    /// </summary>
    /// <param name="configuration">应用程序配置</param>
    /// <param name="logger">日志记录器</param>
    /// <exception cref="InvalidOperationException">无法加载密钥时抛出</exception>
    public KeyProvider(IConfiguration configuration, ILogger<KeyProvider> logger)
    {
        _logger = logger;
        _currentKeyVersion = 1;

        // 确定密钥文件路径
        _keyFilePath = configuration["Encryption:KeyFilePath"] ?? DefaultKeyFilePath;

        // 尝试按优先级加载密钥
        _masterKey = LoadKey(configuration)
                     ?? throw new InvalidOperationException(
                         "Failed to load encryption key. " +
                         "Set FILE_ENCRYPTION_KEY environment variable, " +
                         $"configure a key file at '{_keyFilePath}', " +
                         "or set Encryption:MasterKey in app settings (development only).");

        _logger.LogInformation(
            "Encryption key loaded successfully. KeyVersion={KeyVersion}, KeySource={KeySource}",
            _currentKeyVersion, GetKeySourceDescription());
    }

    /// <inheritdoc />
    public ushort CurrentKeyVersion => _currentKeyVersion;

    /// <inheritdoc />
    public byte[] GetMasterKey(ushort keyVersion = 1)
    {
        if (keyVersion == _currentKeyVersion)
        {
            return _masterKey;
        }

        if (_historicalKeys.TryGetValue(keyVersion, out var historicalKey))
        {
            return historicalKey;
        }

        throw new KeyNotFoundException(
            $"Master key for version {keyVersion} is not available. " +
            $"Current key version is {_currentKeyVersion}.");
    }

    /// <inheritdoc />
    public bool SupportsKeyVersion(ushort version)
    {
        return version == _currentKeyVersion || _historicalKeys.ContainsKey(version);
    }

    /// <summary>
    /// 注册历史密钥（用于密钥轮换时保留旧版本密钥以便解密旧文件）
    /// </summary>
    /// <param name="version">密钥版本</param>
    /// <param name="key">32 字节密钥</param>
    public void RegisterHistoricalKey(ushort version, byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Historical key must be 32 bytes.", nameof(key));

        _historicalKeys[version] = key;
        _logger.LogDebug("Registered historical key version {Version}.", version);
    }

    /// <summary>
    /// 按优先级尝试加载密钥
    /// </summary>
    /// <returns>32 字节密钥，如果所有来源均失败则返回 null</returns>
    private byte[]? LoadKey(IConfiguration configuration)
    {
        // 1. 环境变量
        var envKey = Environment.GetEnvironmentVariable("FILE_ENCRYPTION_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            try
            {
                var key = Convert.FromBase64String(envKey);
                if (key.Length == 32)
                {
                    _logger.LogDebug("Loaded encryption key from environment variable FILE_ENCRYPTION_KEY.");
                    return key;
                }

                _logger.LogWarning(
                    "Environment variable FILE_ENCRYPTION_KEY has invalid length ({Length} bytes, expected 32).",
                    key.Length);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Environment variable FILE_ENCRYPTION_KEY is not valid base64.");
            }
        }

        // 2. 密钥文件
        var keyFromFile = LoadKeyFromFile();
        if (keyFromFile != null)
        {
            return keyFromFile;
        }

        // 3. 配置项（仅开发环境）
        var configKey = configuration["Encryption:MasterKey"];
        if (!string.IsNullOrEmpty(configKey))
        {
            try
            {
                var key = Convert.FromBase64String(configKey);
                if (key.Length == 32)
                {
                    _logger.LogDebug("Loaded encryption key from configuration Encryption:MasterKey.");
                    return key;
                }

                _logger.LogWarning(
                    "Configuration Encryption:MasterKey has invalid length ({Length} bytes, expected 32).",
                    key.Length);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Configuration Encryption:MasterKey is not valid base64.");
            }
        }

        // 4. 首次启动自动生成
        _logger.LogInformation("No existing encryption key found. Generating a new key and saving to {FilePath}.", _keyFilePath);
        return GenerateAndSaveKey();
    }

    /// <summary>
    /// 从密钥文件加载密钥
    /// </summary>
    private byte[]? LoadKeyFromFile()
    {
        try
        {
            if (!System.IO.File.Exists(_keyFilePath))
            {
                _logger.LogDebug("Key file not found at {FilePath}.", _keyFilePath);
                return null;
            }

            var rawData = System.IO.File.ReadAllBytes(_keyFilePath);
            if (rawData.Length == 0)
            {
                _logger.LogWarning("Key file at {FilePath} is empty.", _keyFilePath);
                return null;
            }

            // 支持两种格式：纯二进制 32 字节 或 base64 编码文本
            if (rawData.Length == 32)
            {
                _logger.LogDebug("Loaded encryption key from binary file {FilePath}.", _keyFilePath);
                return rawData;
            }

            // 尝试作为 base64 文本读取
            var text = Encoding.UTF8.GetString(rawData).Trim();
            try
            {
                var key = Convert.FromBase64String(text);
                if (key.Length == 32)
                {
                    _logger.LogDebug("Loaded encryption key from base64 file {FilePath}.", _keyFilePath);
                    return key;
                }
            }
            catch (FormatException)
            {
                // 不是 base64 也不符合长度要求
            }

            _logger.LogWarning(
                "Key file at {FilePath} has invalid format or length ({Length} bytes).",
                _keyFilePath, rawData.Length);
            return null;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Error reading key file at {FilePath}.", _keyFilePath);
            return null;
        }
    }

    /// <summary>
    /// 生成新密钥并保存到文件
    /// </summary>
    private byte[] GenerateAndSaveKey()
    {
        var newKey = new byte[32];
        RandomNumberGenerator.Fill(newKey);

        try
        {
            var directory = Path.GetDirectoryName(_keyFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 以 base64 格式保存密钥文件
            var base64Key = Convert.ToBase64String(newKey);
            System.IO.File.WriteAllText(_keyFilePath, base64Key);

            // 设置文件权限为 0600（仅所有者可读写）
            try
            {
                SetFilePermissions(_keyFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set file permissions on {FilePath}.", _keyFilePath);
            }

            _logger.LogInformation(
                "New encryption key generated and saved to {FilePath}. Key size: 32 bytes.",
                _keyFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save generated key to {FilePath}. Key will be used in-memory only.", _keyFilePath);
        }

        return newKey;
    }

    /// <summary>
    /// 获取密钥来源描述（用于日志）
    /// </summary>
    private string GetKeySourceDescription()
    {
        if (Environment.GetEnvironmentVariable("FILE_ENCRYPTION_KEY") != null)
            return "EnvironmentVariable";

        if (System.IO.File.Exists(_keyFilePath))
            return "KeyFile";

        return "Configuration";
    }

    /// <summary>
    /// 设置文件权限为 0600（仅 Unix 系统）
    /// </summary>
    private static void SetFilePermissions(string filePath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                // 使用 .NET 内置 API 设置文件权限，避免外部进程
                System.IO.File.SetUnixFileMode(filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // 权限设置失败不应该是致命错误
            }
        }
    }
}
