# CLI 运维工具细案

> 用途：说明 FileUploadServer.Web 的命令行运维工具——5 个加密系统管理命令的用途、交互流程、使用场景和运维组合，作为 [01-architecture.md](01-architecture.md) 中 CLI 模式的深化补充。
> 创建：2026-08-02 | 关联：[04-encryption.md](04-encryption.md) / [05-key-management.md](05-key-management.md) / [01-architecture.md](01-architecture.md)

## 目录

1. [CLI 模式概述](#cli-模式概述)
2. [--encrypt-init](#--encrypt-init)
3. [--recover](#--recover)
4. [--encrypt-add-slot](#--encrypt-add-slot)
5. [--encrypt-remove-slot](#--encrypt-remove-slot)
6. [--export-plaintext](#--export-plaintext)
7. [运维组合示例](#运维组合示例)
8. [关键类/文件](#关键类文件)
9. [关联文档](#关联文档)

---

## CLI 模式概述

### 触发机制

来源：`FileUploadServer.Web/Program.cs` 第 77-78 行。

```csharp
var isCliMode = args.Contains("--encrypt-init") || args.Contains("--recover") ||
                args.Contains("--encrypt-add-slot") || args.Contains("--encrypt-remove-slot") ||
                args.Contains("--export-plaintext");
```

当命令行参数匹配以上任一命令时，`isCliMode = true`。CLI 模式下的行为差异：

1. **不注册 KeyRotationService**：跳过 `builder.Services.AddHostedService<KeyRotationService>()`，避免后台密钥轮换服务与 CLI 操作冲突
2. **不启动 Web 服务**：`await EncryptionCommands.TryHandleAsync(args, app.Services)` 返回 `true` 后直接 `return`，不调用 `app.Run()`。DI 容器和数据库连接已初始化完毕，命令执行完成后即退出

### 命令入口

`EncryptionCommands.TryHandleAsync(args, serviceProvider)` → 根据 `args[0]` 分发到对应 handler。

来源：`FileUploadServer.Web/Commands/EncryptionCommands.cs`。

### 5 个命令速览

| 命令 | 参数 | 用途 |
|---|---|---|
| `--encrypt-init` | 无 | 交互式初始化加密系统 |
| `--recover` | 无 | 通过恢复口令重建密钥文件 |
| `--encrypt-add-slot` | 无 | 添加新恢复口令槽位 |
| `--encrypt-remove-slot` | `<索引>` | 移除指定索引的密钥槽 |
| `--export-plaintext` | `<输出目录>` | 批量解密导出所有文件 |

---

## --encrypt-init

### 用途

交互式初始化加密系统。生成 256 位随机 Master Key，创建 Slot 0（自动类型槽位），写入 JSON 密钥文件（权限 0600），并可选设置恢复口令。

### 交互流程

```
$ dotnet FileUploadServer.Web.dll --encrypt-init
=== 加密系统初始化 ===
  → 检查密钥文件是否已存在
  → [已存在] 密钥文件已存在。是否覆盖？(y/N):
  → 生成 Master Key (32字节，RandomNumberGenerator)
  → 生成 Slot 0 包装密钥 (32字节，RandomNumberGenerator)
  → AES-256-GCM 加密 Master Key → 写入键盘文件
✓ Master Key 已生成

建议：设置一个或多个恢复口令...
是否设置恢复口令？(Y/n):
[Y]
请输入恢复口令（留空结束）: ****
请再次输入确认: ****
口令提示（可选）: 用于生产环境的恢复口令
✓ 恢复口令槽位已添加 (索引: 1)
是否继续添加另一个恢复口令？(y/N):

=== 初始化完成 ===
密钥文件: /etc/fileuploadserver/encryption-key.json
密钥版本: 1
密钥槽数: 2

⚠ 重要提示：请立即备份密钥文件和恢复口令！
```

### 底层操作

- `KeySlotManager.InitializeSlots()` → 生成 `byte[32]` Master Key + 包装密钥 → AES-256-GCM 加密 → 写入 `KeyFileData`（Slot 0: type=auto）
- `KeySlotManager.AddPassphraseSlot(passphrase, hint)` → PBKDF2 (SHA256, 600,000 迭代, 32 字节盐) → 派生密钥 → AES-256-GCM 加密 Master Key → 追加 Slot

### 使用场景

- 首次部署加密文件存储
- 密钥文件丢失或损坏后重建（覆盖旧文件）
- 从非加密模式迁移到加密模式

---

## --recover

### 用途

通过恢复口令重建密钥文件。从现有密钥文件的 passphrase 槽位中尝试解密 Master Key，成功后重建 Slot 0（新包装密钥），移除所有 passphrase 槽位。

### 交互流程

```
$ dotnet FileUploadServer.Web.dll --recover
=== 通过恢复口令重建密钥 ===
请输入恢复口令: ****
✓ 密钥恢复成功！密钥文件已重建。
```

### 底层操作

- `KeySlotManager.RecoverByPassphrase(passphrase, rebuildKeyFile: true)`
  1. 加载密钥文件 → 遍历所有 `type=passphrase` 的槽位
  2. 每个槽位：取 salt → PBKDF2 派生密钥 → AES-256-GCM 解密 encryptedMasterKey
  3. 解密成功 → 获取 Master Key → `RebuildKeyFile()`：生成新 Slot 0（新包装密钥），丢弃所有 passphrase 槽位
  4. 所有 passphrase 槽位尝试失败 → `InvalidOperationException`

### 使用场景

- Slot 0 的包装密钥丢失或损坏（密钥文件被意外修改）
- 迁移密钥到新服务器后重建 auto 槽位
- 轮换包装密钥（通过恢复+自动重建实现）

---

## --encrypt-add-slot

### 用途

向现有密钥文件添加新的恢复口令槽位。先通过 Slot 0 解密 Master Key，再用 PBKDF2 派生的新密钥加密后追加到密钥文件。

### 交互流程

```
$ dotnet FileUploadServer.Web.dll --encrypt-add-slot
=== 添加恢复口令槽位 ===
请输入新的恢复口令: ****
请输入确认口令: ****
口令提示（可选，例如用于XXX的恢复口令）: 备用恢复口令 for admin2
✓ 恢复口令槽位已添加 (索引: 2)
```

### 底层操作

- `KeySlotManager.AddPassphraseSlot(passphrase, hint)`
  1. `DecryptMasterKeyFromAnySlot()` → 通过 Slot 0 (auto) 解密 Master Key
  2. 生成 32 字节随机盐 → PBKDF2 派生密钥
  3. AES-256-GCM 加密 Master Key
  4. 追加 `KeySlot(type=passphrase)` 到 `keyFile.Slots`
  5. 保存密钥文件

### 使用场景

- 为不同管理员添加各自的恢复口令
- 在密钥轮换后重新添加恢复口令（轮换会移除所有 passphrase 槽位）
- 定期更换恢复口令

### 注意事项

- 必须先完成 `--encrypt-init`（密钥文件必须存在）
- 两次输入的口令必须一致，否则操作中止
- 口令提示为可选（建议填写，方便区分多个槽位）

---

## --encrypt-remove-slot

### 用途

移除指定索引的密钥槽。不能移除最后一个槽位（至少保留一个）。

### 用法

```
dotnet FileUploadServer.Web.dll --encrypt-remove-slot <索引>
```

### 示例

```
$ dotnet FileUploadServer.Web.dll --encrypt-remove-slot 1
=== 移除密钥槽位 ===
✓ 槽位 1 已移除。
```

### 底层操作

- `KeySlotManager.RemoveSlot(slotIndex)`
  1. 校验 `slotIndex` 范围：`0 ≤ slotIndex < slots.Count`
  2. 校验 `slots.Count > 1`（禁止移除最后一个）
  3. `slots.RemoveAt(slotIndex)` → 保存

### 错误处理

| 错误情况 | 输出 |
|---|---|
| 索引无效 | 错误：无效的槽位索引 |
| 试图移除最后一个槽位 | 错误：无法移除槽位：Cannot remove the last remaining key slot |
| 密钥文件不存在 | 错误：密钥文件不存在 |

### 使用场景

- 移除废弃的恢复口令
- 管理员离职后收回其恢复权限
- 清理无用槽位保持文件整洁

---

## --export-plaintext

### 用途

批量解密导出所有文件到指定目录。遍历数据库中所有 `FileItem` 记录，根据 `EncryptionVersion` 判断是否需要解密，将明文文件输出到目标目录。

### 用法

```
dotnet FileUploadServer.Web.dll --export-plaintext <输出目录>
```

### 示例

```
$ dotnet FileUploadServer.Web.dll --export-plaintext /backup/files
=== 批量解密导出 ===
找到 142 个文件，开始导出...
导出完成！140 个文件成功导出到 /backup/files
警告: 2 个文件导出失败，请查看日志了解详情。
```

### 底层操作

1. `dbContext.Files.ToListAsync()` → 获取所有文件记录
2. 遍历每个文件：
   - 查找物理文件路径（`FindFilePath`，支持 `uploads/{hash[0:2]}/{hash}` 子目录结构）
   - 文件不存在 → 跳过，failCount++
   - `EncryptionVersion >= 1 && DiskFileName 非空` → 包装 `AesGcmDecryptStream` 解密
   - `EncryptionVersion == 0` → 直接复制
   - 输出文件名格式：`{Id}_{sanitized(FileName)}`
3. 统计输出：成功 N 个，失败 M 个

### 输出文件命名

```
{输出目录}/123_report.pdf       (Id=123, FileName="report.pdf")
{输出目录}/456_image.jpg        (Id=456, FileName="image.jpg")
```

文件名中的非法字符被替换为 `_`（通过 `SanitizeFileName`）。

### 使用场景

- 系统迁移（从加密存储切换到非加密存储）
- 数据备份（导出明文副本到安全位置）
- 密钥丢失前的紧急数据抢救
- 审计/合规检查时需要查看文件明文

---

## 运维组合示例

### 场景 1：初始化加密系统 → 设置口令 → 轮换

```bash
# 第一步：初始化
dotnet FileUploadServer.Web.dll --encrypt-init
# 密钥文件生成，设置两个恢复口令（管理员A、管理员B）

# 第二步：运行 Web 服务（自动每日轮换）
dotnet FileUploadServer.Web.dll
# KeyRotationService 每日自动轮换 Master Key

# 第三步：轮换后恢复口令丢失了 → 重新添加
dotnet FileUploadServer.Web.dll --encrypt-add-slot
# 输入新的恢复口令
```

### 场景 2：迁移到新服务器

```bash
# 源服务器：导出所有文件（明文）
dotnet FileUploadServer.Web.dll --export-plaintext /tmp/file_export

# 传输到新服务器
scp -r /tmp/file_export new-server:/tmp/

# 新服务器：初始化加密系统
dotnet FileUploadServer.Web.dll --encrypt-init
```

### 场景 3：紧急恢复

```bash
# Slot 0 包装密钥丢失或损坏 → 通过口令恢复
dotnet FileUploadServer.Web.dll --recover
# 输入恢复口令 → 重建 Slot 0

# 恢复后移除旧的口令槽位
dotnet FileUploadServer.Web.dll --encrypt-remove-slot 1

# 添加新的恢复口令
dotnet FileUploadServer.Web.dll --encrypt-add-slot
```

### 场景 4：管理员权限变更

```bash
# 查看当前槽位数（检查密钥文件 JSON）
cat /etc/fileuploadserver/encryption-key.json | python3 -c "import json,sys; d=json.load(sys.stdin); print(f'{len(d[\"slots\"])} slots')"

# 移除旧管理员的恢复口令
dotnet FileUploadServer.Web.dll --encrypt-remove-slot 1

# 添加新管理员的恢复口令
dotnet FileUploadServer.Web.dll --encrypt-add-slot
```

---

## 关键类/文件

| 文件 | 关键类/方法 | 职责 |
|---|---|---|
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `EncryptionCommands.TryHandleAsync()` | CLI 命令入口，5 个命令分发 |
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `HandleEncryptInitAsync()` | --encrypt-init 处理器 |
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `HandleRecoverAsync()` | --recover 处理器 |
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `HandleAddSlotAsync()` | --encrypt-add-slot 处理器 |
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `HandleRemoveSlotAsync()` | --encrypt-remove-slot 处理器 |
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `HandleExportPlaintextAsync()` | --export-plaintext 处理器 |
| `FileUploadServer.Web/Commands/EncryptionCommands.cs` | `ReadPassword()` | 控制台密码读取（不回显） |
| `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` | `KeySlotManager` | 密钥文件 CRUD 核心逻辑 |
| `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` | `InitializeSlots()` | 初始化密钥槽 |
| `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` | `RecoverByPassphrase()` | 通过口令恢复 Master Key |
| `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` | `AddPassphraseSlot()` | 添加口令槽位 |
| `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` | `RemoveSlot()` | 移除槽位 |
| `FileUploadServer.Infrastructure/Encryption/KeySlotManager.cs` | `RotateKey()` | 轮换 Master Key（Web 服务使用） |
| `FileUploadServer.Core/Interfaces/IKeyProvider.cs` | `IKeyProvider` | 密钥提供者接口 |
| `FileUploadServer.Web/Program.cs` | CLI 模式检测 + 分支 | `isCliMode` 判断 + `EncryptionCommands.TryHandleAsync` 调用 |

---

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览、DI 注册（KeySlotManager Singleton）、中间件管线
- [04-encryption.md](04-encryption.md) — 文件存储加密细案（AES-256-GCM 加密/解密流）
- [05-key-management.md](05-key-management.md) — 密钥生命周期管理（KeySlotManager 完整 API、轮换策略）
