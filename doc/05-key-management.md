# 密钥生命周期细案

> 用途：说明主密钥的加载、存储、恢复、轮换机制，包括 KeyProvider 的 4 级加载优先级、KeySlotManager 的 LUKS 风格多槽位、密钥轮换后台服务及历史密钥解密机制。
> 创建：2026-08-02 | 关联：[04-encryption.md](04-encryption.md) / [01-architecture.md](01-architecture.md)

## 目录

1. [密钥架构概述](#密钥架构概述)
2. [IKeyProvider 接口](#ikeyprovider-接口)
3. [KeyProvider：四级加载优先级](#keyprovider四级加载优先级)
4. [KeySlotManager：LUKS 风格多槽位](#keyslotmanagerluks-风格多槽位)
5. [槽位操作](#槽位操作)
6. [KeyRotationService：密钥轮换后台任务](#keyrotationservice密钥轮换后台任务)
7. [历史密钥解密机制](#历史密钥解密机制)
8. [CLI 命令](#cli-命令)
9. [关键类/文件](#关键类文件)
10. [关联文档](#关联文档)

---

## 1. 密钥架构概述

文件加密系统采用**主密钥 + 密钥槽 + 版本化**的三层架构：

```
┌─────────────────────────────────────────────────────────┐
│                     Master Key (32 bytes)                │
│                     AES-256 对称密钥                      │
│                    版本号递增 (1, 2, 3, ...)              │
├─────────────────────────────────────────────────────────┤
│               Key Slots（密钥槽，LUKS 风格）              │
│  Slot 0 (auto):   Master Key 由随机包装密钥加密存储      │
│  Slot 1-N (passphrase): Master Key 由 PBKDF2 口令加密   │
├─────────────────────────────────────────────────────────┤
│              密钥文件 (JSON, chmod 0600)                  │
│  默认路径: /etc/fileuploadserver/encryption.key          │
│  或配置: Encryption:KeyFilePath                          │
└─────────────────────────────────────────────────────────┘
```

关键设计决策：
- **KeyProvider** 负责运行时提供当前和历史密钥
- **KeySlotManager** 负责密钥文件的持久化和多槽位管理（类似 LUKS）
- **KeyRotationService** 负责定期生成新密钥并迁移旧文件
- **历史密钥保留**：轮换后旧版本密钥保留在 `_historicalKeys` 字典中，确保旧文件仍可解密

---

## 2. IKeyProvider 接口

定义在 `Core/Interfaces/IKeyProvider.cs`：

```csharp
public interface IKeyProvider
{
    byte[] GetMasterKey(ushort keyVersion = 1);  // 获取指定版本密钥
    ushort CurrentKeyVersion { get; }            // 当前活跃密钥版本
    bool SupportsKeyVersion(ushort version);     // 是否支持该版本
}
```

`GetMasterKey()` 优先返回当前版本密钥，如果版本号不匹配则查询历史密钥字典。版本号不匹配且不在历史字典中时抛出 `KeyNotFoundException`。

---

## 3. KeyProvider：四级加载优先级

`KeyProvider`（`Infrastructure/Encryption/KeyProvider.cs`）在构造时按固定优先级加载主密钥：

| 优先级 | 来源 | 说明 |
|---|---|---|
| **1** | 环境变量 `FILE_ENCRYPTION_KEY` | Base64 编码的 32 字节密钥。最优先，适合容器/生产环境。 |
| **2** | 密钥文件 | 路径由 `Encryption:KeyFilePath` 配置指定，默认 `/etc/fileuploadserver/encryption.key`。支持两种格式：纯二进制 32 字节或 Base64 文本。 |
| **3** | 配置项 `Encryption:MasterKey` | 仅开发环境使用，手动配置到 appsettings.json |
| **4** | 首次启动自动生成 | 32 字节随机密钥 → Base64 编码 → 写入密钥文件 → `chmod 0600` |

### 3.1 自动生成流程

```csharp
// GenerateAndSaveKey():
// 1. RandomNumberGenerator.Fill(new byte[32])
// 2. 创建目录（如不存在）
// 3. File.WriteAllText(path, Convert.ToBase64String(newKey))
// 4. Unix: SetUnixFileMode(path, UserRead | UserWrite)  // chmod 0600
// 5. logger 日志记录
```

### 3.2 历史密钥注册

```csharp
public void RegisterHistoricalKey(ushort version, byte[] key)
{
    if (key.Length != 32)
        throw new ArgumentException("Historical key must be 32 bytes.");
    _historicalKeys[version] = key;
}
```

在密钥轮换后，旧版本密钥通过此方法注册到历史字典中，使 `SupportsKeyVersion()` 和 `GetMasterKey()` 可以访问旧版本。

> 源码：`FileUploadServer.Infrastructure/Encryption/KeyProvider.cs:1-292`

---

## 4. KeySlotManager：LUKS 风格多槽位

`KeySlotManager`（`Infrastructure/Encryption/KeySlotManager.cs`）管理 JSON 格式的密钥文件，支持多密钥槽，灵感来自 Linux LUKS 磁盘加密。

### 4.1 密钥文件结构（KeyFileData）

```json
{
  "version": 1,
  "created": "2026-08-02T10:00:00Z",
  "currentKeyVersion": 1,
  "slots": [
    {
      "type": "auto",
      "encryptedMasterKey": "<base64>",
      "salt": "<base64>",
      "iterations": 600000,
      "iv": "<base64 12 bytes>",
      "tag": "<base64 16 bytes>",
      "hint": null,
      "wrappingKey": "<base64 32 bytes>"
    },
    {
      "type": "passphrase",
      "encryptedMasterKey": "<base64>",
      "salt": "<base64 32 bytes>",
      "iterations": 600000,
      "iv": "<base64 12 bytes>",
      "tag": "<base64 16 bytes>",
      "hint": "我的恢复口令提示"
    }
  ]
}
```

### 4.2 KeySlot 字段说明

| 字段 | JSON 键 | 说明 |
|---|---|---|
| `Type` | `type` | `"auto"`（Slot 0，包装密钥加密）或 `"passphrase"`（恢复口令） |
| `EncryptedMasterKey` | `encryptedMasterKey` | 加密后的 Master Key（Base64） |
| `Salt` | `salt` | PBKDF2 盐值（Base64），auto 类型为全 0 占位 |
| `Iterations` | `iterations` | PBKDF2 迭代次数，默认 **600,000** |
| `Iv` | `iv` | GCM 加密 IV，12 字节（Base64） |
| `Tag` | `tag` | GCM 认证标签，16 字节（Base64） |
| `Hint` | `hint` | 口令提示（仅 passphrase 类型） |
| `WrappingKey` | `wrappingKey` | 包装密钥（仅 auto 类型），32 字节（Base64） |

### 4.3 类常量

| 常量 | 值 | 说明 |
|---|---|---|
| `DefaultIterations` | 600,000 | PBKDF2 默认迭代次数 |
| `MasterKeySize` | 32 | Master Key 大小（256 位） |
| `DerivedKeySize` | 32 | 口令派生密钥大小（256 位） |
| `SaltSize` | 32 | 盐值大小 |
| `NonceSize` | 12 | GCM Nonce |
| `TagSize` | 16 | GCM 认证标签 |

> 源码：`FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs:110-168`

---

## 5. 槽位操作

### 5.1 InitializeSlots()：初始化

首次调用时创建密钥文件：
1. 生成随机 32 字节 Master Key
2. 生成随机 32 字节 Wrapping Key（用于 Slot 0）
3. 用 Wrapping Key 加密 Master Key（AES-256-GCM）→ EncryptedMasterKey, IV, Tag
4. 创建 Slot 0（type=auto），存储加密结果和 WrappingKey
5. 保存为 JSON 密钥文件，设置 `chmod 0600`

### 5.2 LoadMasterKey()：加载主密钥

1. 从文件读取 JSON → 反序列化 `KeyFileData`
2. 优先使用 Slot 0（auto 类型）：取出 WrappingKey → 解密 EncryptedMasterKey
3. 如果 Slot 0 解密失败或不存在 → 抛异常提示使用 `RecoverByPassphrase`

### 5.3 AddPassphraseSlot(passphrase, hint)：添加恢复口令槽

1. 从已有槽位解密当前的 Master Key
2. 生成随机 32 字节 Salt
3. `PBKDF2(passphrase, salt, 600000, SHA256)` → 32 字节派生密钥
4. 用派生密钥加密 Master Key（AES-256-GCM）
5. 创建新 `KeySlot`（type=passphrase），追加到槽位列表
6. 保存密钥文件

### 5.4 RemoveSlot(slotIndex)：移除槽位

- 至少保留 1 个槽位（`Slots.Count <= 1` 时抛异常）
- 物理移除指定索引的槽位，保存密钥文件

### 5.5 RecoverByPassphrase(passphrase, rebuildKeyFile)：口令恢复

遍历所有 passphrase 槽位，逐个尝试解密：
1. 从每个 passphrase 槽位取 Salt → `PBKDF2(passphrase, salt, iterations, SHA256)` → 派生密钥
2. 用派生密钥尝试 `DecryptWithKey(encryptedMasterKey, derivedKey, iv, tag)`
3. 如果 GCM 认证失败 → 尝试下一个槽位
4. 如果成功：
   - 清理派生密钥内存（`Array.Clear`）
   - 如果 `rebuildKeyFile=true`：创建新的 Slot 0，删除所有旧槽位

### 5.6 RotateKey()：密钥轮换

1. 生成新随机 32 字节 Master Key
2. `currentKeyVersion += 1`
3. 重新加密所有 auto 槽位（生成新 WrappingKey）
4. **移除所有 passphrase 槽位**（轮换后需用户重新添加）
5. 保存密钥文件

> 源码：`FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs:170-461`

---

## 6. KeyRotationService：密钥轮换后台任务

`KeyRotationService` 是 `BackgroundService` 子类（`Web/Services/KeyRotationService.cs`），作为 Hosted Service 在 Web 服务启动时注册（仅在非 CLI 模式下）。

### 6.1 KeyRotationOptions 配置

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `CheckInterval` | 24 小时 | 轮换检查间隔 |
| `MigrationRatePerMinute` | 100 | 每分钟最大重新加密文件数 |
| `Enabled` | true | 是否启用自动轮换 |
| `KeyRotationDays` | 90 | 生成新密钥后旧密钥可继续使用的天数 |
| `KeyFilePath` | null | 密钥文件路径（不设置则用默认） |

### 6.2 执行流程

```
ExecuteAsync():
  while (!stoppingToken.IsCancellationRequested):
    PerformKeyRotationAsync()
    await Task.Delay(CheckInterval)
```

### 6.3 PerformKeyRotationAsync 详细步骤

1. 从 DI 获取 `AppDbContext`、`IKeyProvider`、`KeySlotManager`、`IWebHostEnvironment`
2. 获取 `currentKeyVersion`
3. 查询 `EncryptionVersion != 1` 或 `KeyVersion != currentKeyVersion` 的文件
4. 按 `MigrationRatePerMinute` 限制每次轮换的文件数量（`.Take()`）
5. 对每个待迁移文件调用 `ReEncryptFileAsync`：
   - 用旧版密钥 + `AesGcmDecryptStream` 解密文件到内存
   - 用新版密钥 + `AesGcmEncryptStream` 重新加密写入新文件
   - `DiskFileName = SHA256(fileId + masterKeyPrefix[0..16]).ToHex()[0..16]`
   - 删除旧文件 + 更新数据库记录（`EncryptionVersion=1`, `KeyVersion=newVersion`, `BlockSize=DefaultBlockSize`, `DiskFileName=newFileName`）
6. 每处理 10 个文件暂停控制迁移速率

### 6.4 DiskFileName 计算

```csharp
// diskFileName = SHA256(fileId + masterKeyPrefix)[0..16].ToHex().ToLower()
var prefix = Convert.ToHexString(masterKey)[..16];  // 取 masterKey 前 16 hex 字符
var input = $"{fileId}:{prefix}";
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
```

### 6.5 文件查找策略

```csharp
// 先尝试子目录格式: uploads/{prefix2}/{fileName}
// 回退到平铺格式: uploads/{fileName}
```

> 源码：`FileUploadServer.Web/Services/KeyRotationService.cs:1-288`

---

## 7. 历史密钥解密机制

当密钥轮换后，新上传的文件使用新版本密钥加密，旧文件保留旧密钥版本。解密流程：

1. `AesGcmDecryptStream.ParseHeader()` 从文件头读取 `KeyVersion`
2. 调用 `keyProvider.SupportsKeyVersion(keyVersion)` 检查
3. 调用 `keyProvider.GetMasterKey(keyVersion)` 获取对应版本密钥
4. 如果旧版本密钥已通过 `RegisterHistoricalKey()` 注册 → 正常解密
5. 如果旧版本密钥不可用 → 抛 `KeyNotFoundException` 或 `CryptographicException`

> 关键点：轮换后**不能删除旧版本密钥**，否则使用旧密钥加密的文件将永久不可读。`KeyProvider._historicalKeys` 字典负责持久化这一关系（注：当前实现中历史密钥为内存字典，需确保每次启动后通过 KeySlotManager 的 KeyFileData 重新加载）。

---

## 8. CLI 命令

由 `Web/Commands/EncryptionCommands.cs` 处理，在 `Program.cs` 主流程中通过 `EncryptionCommands.TryHandleAsync(args, app.Services)` 在 Web 服务启动前拦截执行：

| 命令 | 说明 |
|---|---|
| `--encrypt-init` | 初始化加密系统，生成 Master Key，创建密钥文件和 Slot 0 |
| `--recover` | 通过恢复口令重建 Master Key |
| `--encrypt-add-slot` | 添加恢复口令槽位 |
| `--encrypt-remove-slot` | 移除指定密钥槽 |
| `--export-plaintext` | 导出明文 Master Key（危险操作） |

CLI 模式下不会启动 `KeyRotationService` 后台任务：

```csharp
var isCliMode = args.Contains("--encrypt-init") || args.Contains("--recover") || ...
if (!isCliMode)
{
    builder.Services.AddHostedService<KeyRotationService>();
}
```

---

## 9. 关键类/文件

| 类/文件 | 路径 |
|---|---|
| `IKeyProvider` | `FileUploadServer.Core/Interfaces/IKeyProvider.cs` |
| `KeyProvider` | `FileUploadServer.Infrastructure/Encryption/KeyProvider.cs` |
| `KeySlotManager` | `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` |
| `KeySlot` | `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` (line 28) |
| `KeyFileData` | `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` (line 83) |
| `KeySlotType` 枚举 | `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` (line 12) |
| `KeyRotationService` | `FileUploadServer.Web/Services/KeyRotationService.cs` |
| `KeyRotationOptions` | `FileUploadServer.Web/Services/KeyRotationService.cs` (line 12) |
| `EncryptionCommands` | `FileUploadServer.Web/Commands/EncryptionCommands.cs` |

---

## 10. 关联文档

- [04-encryption.md](04-encryption.md) -- AES-256-GCM 加密流实现，使用 IKeyProvider 获取密钥
- [01-architecture.md](01-architecture.md) -- DI 注册（KeyProvider/KeySlotManager 注册为 Singleton，KeyRotationService 注册为 HostedService）
- [03-permission.md](03-permission.md) -- 权限与密钥管理无关
