using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FileUploadServer.Infrastructure.Encryption;

/// <summary>
/// 密钥槽类型
/// </summary>
public enum KeySlotType
{
    /// <summary>
    /// 自动生成密钥的槽位（Slot 0）
    /// </summary>
    Auto,

    /// <summary>
    /// 恢复口令槽位（Slot 1+）
    /// </summary>
    Passphrase
}

/// <summary>
/// 单个密钥槽数据
/// </summary>
public class KeySlot
{
    /// <summary>
    /// 槽位类型
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "auto";

    /// <summary>
    /// 加密后的 Master Key（base64 编码）
    /// </summary>
    [JsonPropertyName("encryptedMasterKey")]
    public string EncryptedMasterKey { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 盐值（base64 编码）
    /// </summary>
    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2 迭代次数
    /// </summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; } = 600_000;

    /// <summary>
    /// GCM 加密 IV（base64 编码，12 字节）
    /// </summary>
    [JsonPropertyName("iv")]
    public string Iv { get; set; } = string.Empty;

    /// <summary>
    /// GCM 认证标签（base64 编码，16 字节）
    /// </summary>
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// 口令提示（仅 passphrase 类型）
    /// </summary>
    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    /// <summary>
    /// 自动槽位的包装密钥（base64 编码，32 字节；仅 auto 类型）
    /// 该密钥用于解密 EncryptedMasterKey，存储在密钥文件中
    /// </summary>
    [JsonPropertyName("wrappingKey")]
    public string? WrappingKey { get; set; }
}

/// <summary>
/// 密钥文件数据结构
/// </summary>
public class KeyFileData
{
    /// <summary>
    /// 密钥文件格式版本
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// 创建时间
    /// </summary>
    [JsonPropertyName("created")]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 当前激活的密钥版本号
    /// </summary>
    [JsonPropertyName("currentKeyVersion")]
    public ushort CurrentKeyVersion { get; set; } = 1;

    /// <summary>
    /// 密钥槽列表
    /// </summary>
    [JsonPropertyName("slots")]
    public List<KeySlot> Slots { get; set; } = new();
}

/// <summary>
/// 密钥槽管理器（LUKS 风格）
/// 管理 JSON 格式的密钥文件，支持多密钥槽：
/// - Slot 0: 自动生成的加密 Master Key（通过存储的包装密钥解密）
/// - Slot 1-N: 恢复口令槽（通过 PBKDF2 口令派生密钥解密）
/// </summary>
public class KeySlotManager
{
    private readonly string _keyFilePath;
    private readonly ILogger<KeySlotManager> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// PBKDF2 默认迭代次数
    /// </summary>
    public const int DefaultIterations = 600_000;

    /// <summary>
    /// Master Key 大小（32 字节 = 256 位）
    /// </summary>
    public const int MasterKeySize = 32;

    /// <summary>
    /// 派生密钥大小（32 字节 = 256 位）
    /// </summary>
    public const int DerivedKeySize = 32;

    /// <summary>
    /// 盐值大小（32 字节）
    /// </summary>
    public const int SaltSize = 32;

    /// <summary>
    /// GCM Nonce 大小（12 字节）
    /// </summary>
    public const int NonceSize = 12;

    /// <summary>
    /// GCM 认证标签大小（16 字节）
    /// </summary>
    public const int TagSize = 16;

    /// <summary>
    /// 初始化密钥槽管理器
    /// </summary>
    /// <param name="keyFilePath">密钥文件路径</param>
    /// <param name="logger">日志记录器</param>
    public KeySlotManager(string keyFilePath, ILogger<KeySlotManager> logger)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
            throw new ArgumentException("Key file path cannot be empty.", nameof(keyFilePath));

        _keyFilePath = keyFilePath;
        _logger = logger;
    }

    /// <summary>
    /// 密钥文件路径
    /// </summary>
    public string KeyFilePath => _keyFilePath;

    /// <summary>
    /// 初始化密钥槽（创建密钥文件）
    /// 生成新的 Master Key，创建 Slot 0（自动类型）并用包装密钥加密
    /// </summary>
    /// <returns>生成的 Master Key</returns>
    public byte[] InitializeSlots()
    {
        if (File.Exists(_keyFilePath))
        {
            _logger.LogWarning("Key file already exists at {FilePath}. Will be overwritten.", _keyFilePath);
        }

        // 生成随机 Master Key
        byte[] masterKey = new byte[MasterKeySize];
        RandomNumberGenerator.Fill(masterKey);

        // 生成 Slot 0 的包装密钥
        byte[] wrappingKey = new byte[MasterKeySize];
        RandomNumberGenerator.Fill(wrappingKey);

        // 用包装密钥加密 Master Key
        var (encryptedMasterKey, iv, tag) = EncryptWithKey(masterKey, wrappingKey);

        var slot0 = new KeySlot
        {
            Type = "auto",
            EncryptedMasterKey = Convert.ToBase64String(encryptedMasterKey),
            Salt = Convert.ToBase64String(new byte[SaltSize]), // auto 类型不使用 salt
            Iterations = DefaultIterations,
            Iv = Convert.ToBase64String(iv),
            Tag = Convert.ToBase64String(tag),
            WrappingKey = Convert.ToBase64String(wrappingKey)
        };

        var keyFile = new KeyFileData
        {
            Version = 1,
            Created = DateTime.UtcNow,
            CurrentKeyVersion = 1,
            Slots = new List<KeySlot> { slot0 }
        };

        SaveKeyFile(keyFile);

        _logger.LogInformation(
            "Key slots initialized. Master Key generated, Slot 0 (auto) created at {FilePath}.",
            _keyFilePath);

        return masterKey;
    }

    /// <summary>
    /// 从密钥文件中提取当前的 Master Key
    /// 尝试所有可用槽位，优先使用 Slot 0（自动类型）
    /// </summary>
    /// <returns>32 字节的 Master Key</returns>
    /// <exception cref="InvalidOperationException">无法解密 Master Key 时抛出</exception>
    public byte[] LoadMasterKey()
    {
        var keyFile = LoadKeyFile();

        if (keyFile.Slots.Count == 0)
        {
            throw new InvalidOperationException("No key slots available in the key file.");
        }

        // 尝试 Slot 0（自动类型）
        var slot0 = keyFile.Slots.FirstOrDefault(s => s.Type == "auto");
        if (slot0 != null && !string.IsNullOrEmpty(slot0.WrappingKey))
        {
            try
            {
                var wrappingKey = Convert.FromBase64String(slot0.WrappingKey);
                var encryptedMasterKey = Convert.FromBase64String(slot0.EncryptedMasterKey);
                var iv = Convert.FromBase64String(slot0.Iv);
                var tag = Convert.FromBase64String(slot0.Tag);

                return DecryptWithKey(encryptedMasterKey, wrappingKey, iv, tag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt Master Key using Slot 0.");
                throw new InvalidOperationException("Failed to decrypt Master Key using Slot 0.", ex);
            }
        }

        // 如果没有 Slot 0，尝试有口令提示的槽位（需要外部提供口令）
        throw new InvalidOperationException(
            "No auto-type key slot (Slot 0) available. Use RecoverByPassphrase to recover.");
    }

    /// <summary>
    /// 添加一个恢复口令槽位
    /// </summary>
    /// <param name="passphrase">恢复口令</param>
    /// <param name="hint">口令提示（可选）</param>
    /// <returns>添加的槽位索引</returns>
    /// <exception cref="InvalidOperationException">密钥文件不存在或口令为空</exception>
    public int AddPassphraseSlot(string passphrase, string? hint = null)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("Passphrase cannot be empty.", nameof(passphrase));

        var keyFile = LoadKeyFile();

        // 提取 Master Key
        byte[] masterKey;
        try
        {
            masterKey = DecryptMasterKeyFromAnySlot(keyFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cannot add passphrase slot: failed to decrypt Master Key.", ex);
        }

        // 从口令派生加密密钥
        byte[] salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        byte[] derivedKey = DeriveKeyFromPassphrase(passphrase, salt, DefaultIterations);

        // 加密 Master Key
        var (encryptedMasterKey, iv, tag) = EncryptWithKey(masterKey, derivedKey);

        var slot = new KeySlot
        {
            Type = "passphrase",
            EncryptedMasterKey = Convert.ToBase64String(encryptedMasterKey),
            Salt = Convert.ToBase64String(salt),
            Iterations = DefaultIterations,
            Iv = Convert.ToBase64String(iv),
            Tag = Convert.ToBase64String(tag),
            Hint = hint
        };

        keyFile.Slots.Add(slot);
        SaveKeyFile(keyFile);

        int slotIndex = keyFile.Slots.Count - 1;
        _logger.LogInformation(
            "Passphrase slot added at index {SlotIndex} with hint: {Hint}.",
            slotIndex, hint ?? "(no hint)");

        return slotIndex;
    }

    /// <summary>
    /// 移除指定索引的密钥槽
    /// </summary>
    /// <param name="slotIndex">要移除的槽位索引</param>
    /// <exception cref="ArgumentOutOfRangeException">索引无效</exception>
    /// <exception cref="InvalidOperationException">尝试移除最后一个槽位</exception>
    public void RemoveSlot(int slotIndex)
    {
        var keyFile = LoadKeyFile();

        if (slotIndex < 0 || slotIndex >= keyFile.Slots.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"Slot index {slotIndex} is out of range. Valid range: 0-{keyFile.Slots.Count - 1}.");
        }

        if (keyFile.Slots.Count <= 1)
        {
            throw new InvalidOperationException(
                "Cannot remove the last remaining key slot. At least one slot must exist.");
        }

        var removedSlot = keyFile.Slots[slotIndex];
        keyFile.Slots.RemoveAt(slotIndex);

        SaveKeyFile(keyFile);

        _logger.LogInformation(
            "Removed {SlotType} slot at index {SlotIndex}.",
            removedSlot.Type, slotIndex);
    }

    /// <summary>
    /// 通过恢复口令重建 Master Key
    /// 验证口令后返回 Master Key，可选择重建密钥文件
    /// </summary>
    /// <param name="passphrase">恢复口令</param>
    /// <param name="rebuildKeyFile">是否重建密钥文件（将 Master Key 迁移到新的 Slot 0）</param>
    /// <returns>Master Key</returns>
    /// <exception cref="InvalidOperationException">口令不正确或没有匹配的槽位</exception>
    public byte[] RecoverByPassphrase(string passphrase, bool rebuildKeyFile = false)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("Passphrase cannot be empty.", nameof(passphrase));

        var keyFile = LoadKeyFile();

        // 尝试所有 passphrase 槽位
        foreach (var slot in keyFile.Slots.Where(s => s.Type == "passphrase"))
        {
            try
            {
                var salt = Convert.FromBase64String(slot.Salt);
                var derivedKey = DeriveKeyFromPassphrase(passphrase, salt, slot.Iterations);
                var encryptedMasterKey = Convert.FromBase64String(slot.EncryptedMasterKey);
                var iv = Convert.FromBase64String(slot.Iv);
                var tag = Convert.FromBase64String(slot.Tag);

                var masterKey = DecryptWithKey(encryptedMasterKey, derivedKey, iv, tag);

                // 清理派生密钥
                Array.Clear(derivedKey, 0, derivedKey.Length);

                _logger.LogInformation(
                    "Master Key recovered successfully using passphrase slot with hint: {Hint}.",
                    slot.Hint ?? "(no hint)");

                if (rebuildKeyFile)
                {
                    RebuildKeyFile(keyFile, masterKey);
                }

                return masterKey;
            }
            catch (CryptographicException)
            {
                // 口令错误，尝试下一个槽位
                continue;
            }
        }

        throw new InvalidOperationException(
            "Failed to recover Master Key: no passphrase slot matched the provided passphrase.");
    }

    /// <summary>
    /// 轮换 Master Key
    /// 生成新的 Master Key，更新所有槽位
    /// </summary>
    /// <returns>新的 Master Key</returns>
    public byte[] RotateKey()
    {
        var keyFile = LoadKeyFile();

        // 生成新的 Master Key
        byte[] newMasterKey = new byte[MasterKeySize];
        RandomNumberGenerator.Fill(newMasterKey);

        // 更新密钥版本
        ushort newVersion = (ushort)(keyFile.CurrentKeyVersion + 1);
        keyFile.CurrentKeyVersion = newVersion;

        // 重新加密所有槽位
        foreach (var slot in keyFile.Slots)
        {
            byte[] slotKey;
            byte[] salt;

            if (slot.Type == "auto")
            {
                // 生成新的包装密钥
                slotKey = new byte[MasterKeySize];
                RandomNumberGenerator.Fill(slotKey);
                salt = new byte[SaltSize]; // auto 不使用 salt
                slot.WrappingKey = Convert.ToBase64String(slotKey);
                slot.Salt = Convert.ToBase64String(salt);
            }
            else
            {
                // passphrase 类型无法自动轮换，需要重新添加
                continue;
            }

            var (encryptedMasterKey, iv, tag) = EncryptWithKey(newMasterKey, slotKey);
            slot.EncryptedMasterKey = Convert.ToBase64String(encryptedMasterKey);
            slot.Iv = Convert.ToBase64String(iv);
            slot.Tag = Convert.ToBase64String(tag);
        }

        // 移除所有 passphrase 槽位（轮换后需要用户重新添加）
        keyFile.Slots.RemoveAll(s => s.Type == "passphrase");

        SaveKeyFile(keyFile);

        _logger.LogInformation(
            "Master Key rotated. New KeyVersion={NewVersion}. Passphrase slots were removed and need to be re-added.",
            newVersion);

        return newMasterKey;
    }

    /// <summary>
    /// 获取密钥文件中的槽位数量
    /// </summary>
    public int SlotCount
    {
        get
        {
            if (!File.Exists(_keyFilePath)) return 0;
            var keyFile = LoadKeyFile();
            return keyFile.Slots.Count;
        }
    }

    /// <summary>
    /// 获取当前密钥文件中记录的密钥版本
    /// </summary>
    public ushort CurrentKeyVersion
    {
        get
        {
            if (!File.Exists(_keyFilePath)) return 0;
            var keyFile = LoadKeyFile();
            return keyFile.CurrentKeyVersion;
        }
    }

    /// <summary>
    /// 使用指定密钥加密数据（AES-256-GCM）
    /// </summary>
    private static (byte[] ciphertext, byte[] iv, byte[] tag) EncryptWithKey(byte[] plaintext, byte[] key)
    {
        byte[] iv = new byte[NonceSize];
        RandomNumberGenerator.Fill(iv);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

        return (ciphertext, iv, tag);
    }

    /// <summary>
    /// 使用指定密钥解密数据（AES-256-GCM）
    /// </summary>
    private static byte[] DecryptWithKey(byte[] ciphertext, byte[] key, byte[] iv, byte[] tag)
    {
        byte[] plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(iv, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// 使用 PBKDF2 从口令派生加密密钥
    /// </summary>
    private static byte[] DeriveKeyFromPassphrase(string passphrase, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            DerivedKeySize);
    }

    /// <summary>
    /// 从任意可用槽位解密 Master Key（用于添加新槽位时）
    /// </summary>
    private byte[] DecryptMasterKeyFromAnySlot(KeyFileData keyFile)
    {
        // 优先使用 Slot 0（auto）
        var autoSlot = keyFile.Slots.FirstOrDefault(s => s.Type == "auto");
        if (autoSlot != null && !string.IsNullOrEmpty(autoSlot.WrappingKey))
        {
            var wrappingKey = Convert.FromBase64String(autoSlot.WrappingKey);
            var encryptedMasterKey = Convert.FromBase64String(autoSlot.EncryptedMasterKey);
            var iv = Convert.FromBase64String(autoSlot.Iv);
            var tag = Convert.FromBase64String(autoSlot.Tag);
            return DecryptWithKey(encryptedMasterKey, wrappingKey, iv, tag);
        }

        throw new InvalidOperationException(
            "Cannot decrypt Master Key: no suitable key slot available.");
    }

    /// <summary>
    /// 从文件加载密钥数据
    /// </summary>
    private KeyFileData LoadKeyFile()
    {
        if (!File.Exists(_keyFilePath))
        {
            throw new InvalidOperationException(
                $"Key file not found at {_keyFilePath}. Run --encrypt-init first.");
        }

        try
        {
            var json = File.ReadAllText(_keyFilePath);
            var keyFile = JsonSerializer.Deserialize<KeyFileData>(json, JsonOptions);

            if (keyFile == null)
            {
                throw new InvalidOperationException($"Failed to parse key file at {_keyFilePath}: deserialization returned null.");
            }

            return keyFile;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse key file at {_keyFilePath}: invalid JSON format.", ex);
        }
    }

    /// <summary>
    /// 保存密钥数据到文件
    /// </summary>
    private void SaveKeyFile(KeyFileData keyFile)
    {
        var directory = Path.GetDirectoryName(_keyFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(keyFile, JsonOptions);
        File.WriteAllText(_keyFilePath, json);

        // 设置文件权限 0600（仅 Unix）
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                System.IO.File.SetUnixFileMode(_keyFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set file permissions on {FilePath}.", _keyFilePath);
        }
    }

    /// <summary>
    /// 重建密钥文件（恢复后使用）
    /// 创建一个新的 Slot 0，移除所有 passphrase 槽位
    /// </summary>
    private void RebuildKeyFile(KeyFileData oldKeyFile, byte[] masterKey)
    {
        byte[] newWrappingKey = new byte[MasterKeySize];
        RandomNumberGenerator.Fill(newWrappingKey);

        var (encryptedMasterKey, iv, tag) = EncryptWithKey(masterKey, newWrappingKey);

        var newKeyFile = new KeyFileData
        {
            Version = oldKeyFile.Version,
            Created = oldKeyFile.Created,
            CurrentKeyVersion = oldKeyFile.CurrentKeyVersion,
            Slots = new List<KeySlot>
            {
                new KeySlot
                {
                    Type = "auto",
                    EncryptedMasterKey = Convert.ToBase64String(encryptedMasterKey),
                    Salt = Convert.ToBase64String(new byte[SaltSize]),
                    Iterations = DefaultIterations,
                    Iv = Convert.ToBase64String(iv),
                    Tag = Convert.ToBase64String(tag),
                    WrappingKey = Convert.ToBase64String(newWrappingKey)
                }
            }
        };

        SaveKeyFile(newKeyFile);

        _logger.LogInformation("Key file rebuilt with new Slot 0.");
    }
}
