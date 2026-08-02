# 文件存储加密细案

> 用途：说明 AES-256-GCM 分块加密的文件格式、加密/解密流实现、与存储策略的集成方式，以及加密对用户透明的设计原理。
> 创建：2026-08-02 | 关联：[03-permission.md](03-permission.md) / [05-key-management.md](05-key-management.md) / [01-architecture.md](01-architecture.md)

## 目录

1. [设计概述](#设计概述)
2. [加密文件格式](#加密文件格式)
3. [AesGcmEncryptStream：加密写入流](#aesgcmencryptstream加密写入流)
4. [AesGcmDecryptStream：解密读取流](#aesgcmdecryptstream解密读取流)
5. [与存储策略的集成](#与存储策略的集成)
6. [常量表：EncryptedFileConstants](#常量表encryptedfileconstants)
7. [对用户透明：上传/下载流程](#对用户透明上传下载流程)
8. [已知问题](#已知问题)
9. [关键类/文件](#关键类文件)
10. [关联文档](#关联文档)

---

## 1. 设计概述

文件服务器在文件落盘时通过 **AES-256-GCM 分块加密**透明保护数据，防止磁盘数据泄露和恶意文件执行。核心设计原则：

- **透明性**：上传时自动加密、下载时自动解密，调用方无需感知
- **分块存储**：大文件按块（默认 1MB）加密，支持 O(1) 块级随机访问（Seek）
- **认证加密**：GCM 模式同时提供机密性和完整性保护（Authentication Tag）
- **密钥版本化**：支持密钥轮换，旧版本密钥可保留用于历史文件解密（见 [05-key-management.md](05-key-management.md)）

---

## 2. 加密文件格式

每个加密文件的磁盘格式为 **48 字节文件头 + 数据块序列**：

```
┌──────────────────────────────────────────────────────────────┐
│  Header (48 bytes)                                           │
│  ┌───────┬────────┬───────────┬──────────┬─────────────────┐│
│  │Magic 4│Version2│KeyVersion2│BlockSize4│  Reserved 36B   ││
│  │"FUEC" │ 0x0001 │   uint16  │  uint32  │   (zeros)       ││
│  └───────┴────────┴───────────┴──────────┴─────────────────┘│
├──────────────────────────────────────────────────────────────┤
│  Chunk[0]                                                    │
│  ┌──────────┬──────────────────┬────────────┐               │
│  │Nonce 12B │ Ciphertext (N B) │ AuthTag 16B│               │
│  └──────────┴──────────────────┴────────────┘               │
├──────────────────────────────────────────────────────────────┤
│  Chunk[1] ...                                                │
└──────────────────────────────────────────────────────────────┘
```

### 2.1 文件头字段

| 偏移 | 大小 | 字段 | 值/说明 |
|---|---|---|---|
| 0 | 4 | Magic | `0x46 0x55 0x45 0x43` = `"FUEC"` (FileUpload Encrypted) |
| 4 | 2 | FormatVersion | `0x0001`，大端序 |
| 6 | 2 | KeyVersion | 用于解密此文件的密钥版本号，大端序 |
| 8 | 4 | BlockSize | 每块明文大小（字节），大端序，默认 `0x00100000` (1,048,576) |
| 12 | 36 | Reserved | 保留字段，全部置 0 |

### 2.2 数据块格式

每个数据块 = **Nonce (12B) + Ciphertext (变长) + AuthTag (16B)**，每块 overhead 固定为 28 字节（Nonce 12 + Tag 16）。

> 关键属性：加密后文件大小 = 48 + ceil(明文大小 / BlockSize) * (BlockSize + 28)。最后一块的 Ciphertext 长度等于剩余的明文字节数（可能不满 BlockSize）。

> 源码：`FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs:8-47`

---

## 3. AesGcmEncryptStream：加密写入流

`AesGcmEncryptStream` 是只写流（`CanWrite=true`, `CanRead=false`, `CanSeek=false`），通过 `Write()` 接收明文并在内部缓冲和加密后写入底层流。

### 3.1 构造参数

| 参数 | 类型 | 说明 |
|---|---|---|
| `innerStream` | `Stream` | 接收加密数据的底层流（如 FileStream） |
| `masterKey` | `byte[]` | 32 字节 AES-256 密钥 |
| `keyVersion` | `ushort` | 密钥版本号（默认 1） |
| `blockSize` | `int` | 每块明文大小，默认 1,048,576 (1MB) |
| `logger` | `ILogger?` | 可选日志记录器 |

构造函数立即在 `innerStream` 中预占 48 字节的文件头空间（写入全 0 占位）。

### 3.2 Write 流程

```
Write(buffer, offset, count):
  循环填充 pendingBuffer（大小 = blockSize）:
    1. 计算 space = blockSize - pendingCount
    2. 复制 min(remaining, space) 到 pendingBuffer
    3. 如果 pendingBuffer 满了（pendingCount >= blockSize）:
       → EncryptAndWriteBlock(final: false)
    4. 继续下一轮
```

### 3.3 EncryptAndWriteBlock（加密单块）

```csharp
// 1. 生成随机 Nonce（12 字节）
byte[] nonce = new byte[12];
RandomNumberGenerator.Fill(nonce);

// 2. 使用 AES-256-GCM 加密
using var aesGcm = new AesGcm(_masterKey, 16);
aesGcm.Encrypt(nonce, _pendingBuffer[0..plaintextLength], ciphertext, tag);

// 3. 写入：[Nonce 12B][Ciphertext][AuthTag 16B]
_innerStream.Write(nonce);
_innerStream.Write(ciphertext);
_innerStream.Write(tag);
```

### 3.4 Flush / Dispose：回写文件头

`Flush()` 或 `Dispose()` 时：
1. 如果还有残留数据（`pendingCount > 0`），作为最后一块加密写入
2. 如果整个流未写入任何数据，仍写一个空块确保格式合法
3. **回写真实文件头**：定位到 `innerStream.Position = 0`，写入包含正确 Magic/Version/KeyVersion/BlockSize 的 48 字节头，然后恢复原写入位置

```csharp
private void WriteHeader()
{
    byte[] header = new byte[48];
    // Magic "FUEC"
    header[0]=0x46; header[1]=0x55; header[2]=0x45; header[3]=0x43;
    // Version 0x0001 (big-endian)
    header[4]=(byte)((0x0001>>8)&0xFF); header[5]=(byte)(0x0001&0xFF);
    // KeyVersion (big-endian)
    header[6]=(byte)((_keyVersion>>8)&0xFF); header[7]=(byte)(_keyVersion&0xFF);
    // BlockSize (big-endian)
    header[8]=(byte)((_blockSize>>24)&0xFF); ...
    // 回写
    long savedPos = _innerStream.Position;
    _innerStream.Position = 0;
    _innerStream.Write(header);
    _innerStream.Position = savedPos;
}
```

> 源码：`FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs:55-288`

---

## 4. AesGcmDecryptStream：解密读取流

`AesGcmDecryptStream` 是只读流（`CanRead=true`, `CanWrite=false`, `CanSeek` 取决于底层流），通过 `Read()` 返回解密后的明文。

### 4.1 构造参数

| 参数 | 类型 | 说明 |
|---|---|---|
| `innerStream` | `Stream` | 读取加密数据的底层流 |
| `keyProvider` | `IKeyProvider` | 密钥提供者（按 KeyVersion 获取 MasterKey） |
| `logger` | `ILogger?` | 可选日志记录器 |

### 4.2 ParseHeader（首次读取/Seek 时触发）

```csharp
// 1. 读取 48 字节文件头
// 2. 验证 Magic = "FUEC"（不是则抛 CryptographicException）
// 3. 验证 FormatVersion = 0x0001
// 4. 读取 KeyVersion + BlockSize
// 5. 调用 keyProvider.SupportsKeyVersion(keyVersion) 验证密钥可用
// 6. 调用 keyProvider.GetMasterKey(keyVersion) 获取解密密钥
```

### 4.3 Read 流程

```
Read(buffer, offset, count):
  EnsureHeaderParsed()                           // 首次调用解析文件头
  循环填充输出缓冲区:
    如果当前块已读完:
      LoadNextChunk()                            // 读取并解密下一块
      如果无更多数据 → 退出循环
    从当前解密块复制 min(remaining, available) 到输出
```

### 4.4 LoadNextChunk（解密单块）

```csharp
// 1. 读取 Nonce (12B)
// 2. 读取 Ciphertext + Tag（读取到 blockSize+16B 或文件末尾）
// 3. 分离密文和认证标签（最后 16B 为 Tag）
// 4. 解密：AesGcm.Decrypt(nonce, ciphertext, tag, plaintext)
//    - 认证失败抛 CryptographicException: "authentication tag mismatch"
//    - 表示文件被篡改或损坏
// 5. 记录当前块索引和数据长度（最后一块可能不满 blockSize）
```

### 4.5 Seek 支持

支持前向 Seek（`SeekOrigin.Begin`/`Current`/`End`），利用分块结构实现块级定位：

```csharp
// 1. 计算目标块索引：targetChunk = newPosition / blockSize
// 2. 块内偏移：targetOffsetInChunk = newPosition % blockSize
// 3. 如果在同一块内（targetChunk == currentChunkIndex）：
//    → 直接调整 chunkOffset
// 4. 如果需要跳转到其他块：
//    → innerStream.Seek(48 + targetChunk * (blockSize + 28))
//    → LoadNextChunk()
//    → 设置 chunkOffset = targetOffsetInChunk
```

注意：Seek 不支持反向（需要从文件头重新定位），每次跳块都需要解密目标块的完整密文。

> 源码：`FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs:295-682`

---

## 5. 与存储策略的集成

### 5.1 LocalStorageStrategy

当前 `LocalStorageStrategy`（`Web/Services/LocalStorageStrategy.cs`）的加密集成处于 **TODO 状态**：

```csharp
// ReadAsync: 加密解密流集成（代码已注释，标有 TODO Phase 1.5）
// if (EncryptionEnabled) {
//     var keyProvider = _serviceProvider.GetRequiredService<IKeyProvider>();
//     fileStream = new AesGcmDecryptStream(fileStream, keyProvider);
// }

// WriteAsync: 加密流集成（代码已注释，标有 TODO Phase 1.5）
// if (EncryptionEnabled) {
//     var keyProvider = _serviceProvider.GetRequiredService<IKeyProvider>();
//     fileStream = new AesGcmEncryptStream(fileStream, keyProvider);
// }
```

> 设计意图（来自 IMPLEMENTATION_PLAN Phase 1.5）：上传流程应通过 `FileApiController` → `StorageStrategy.WriteAsync` → `AesGcmEncryptStream` 完成透明加密；下载流程反向解密。当前加密逻辑可能通过 `KeyRotationService.ReEncryptFileAsync` 中的直接使用 `AesGcmEncryptStream`/`AesGcmDecryptStream` 实现（见 [05-key-management.md](05-key-management.md)），而非通过 `LocalStorageStrategy` 的 TODO 代码路径。

### 5.2 统一解密：FileDownloadService

三个下载入口（网页 `Download.cshtml.cs`、API `FileApiController.Download`、公共访问 `PublicFileMiddleware`）通过共享 `FileDownloadService.OpenDecryptedStreamAsync`（`Web/Services/FileDownloadService.cs`）统一「读取（WS/本地）+ 透明解密」：

- 若 `file.EncryptionVersion > 0` 且 `keyProvider.SupportsKeyVersion(file.KeyVersion)`，返回 `AesGcmDecryptStream` 透明解密流
- 否则返回原始字节流（加密未初始化 / 密钥版本不支持时降级明文，与既有行为一致）
- 磁盘路径解析统一为 `FileDownloadService.ResolveDiskPath`（加密文件用子目录 + DiskFileName，明文用 StoredFileName）

**历史问题**：部分老文件（7-11 及 8-02 上传）用已丢失密钥加密，解密时 tag mismatch，属**数据层问题**（非代码 bug），需用户重新上传。详见 [06-public-access.md](06-public-access.md) 与 [14-dev-log.md](14-dev-log.md)。

---

## 6. 常量表：EncryptedFileConstants

`FileUploadServer.Infrastructure.Encryption.EncryptedFileConstants` 定义所有加密格式常量：

| 常量 | 值 | 说明 |
|---|---|---|
| `Magic` | `"FUEC"` | 文件魔数，4 字节 |
| `FormatVersion` | `0x0001` | 当前格式版本 |
| `HeaderSize` | `48` | 文件头总字节数 |
| `NonceSize` | `12` | GCM Nonce 大小（AES-GCM 标准） |
| `AuthTagSize` | `16` | GCM 认证标签大小 |
| `DefaultBlockSize` | `1_048_576` | 默认分块大小（1 MB） |
| `ChunkOverhead` | `28` | 每块开销 = NonceSize(12) + AuthTagSize(16) |

> 源码：`FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs:11-47`

---

## 7. 对用户透明：上传/下载流程

加密对 API 调用方完全透明：

| 操作 | 存储层面 | 对调用方 |
|---|---|---|
| 上传 (`POST /api/files`) | 明文 → `AesGcmEncryptStream` → 加密写入磁盘 | 无变化 |
| 下载 (`GET /api/files/download/{id}`) | 磁盘密文 → `AesGcmDecryptStream` → 明文返回 | 无变化 |
| 列表 (`GET /api/files`) | 仅查询元数据（不读文件内容） | 无变化 |
| 删除 (`DELETE /api/files/{id}`) | 删除加密文件 + 清理子目录 | 无变化 |

### 7.1 FileItem 加密相关字段

在 `Core/Entities/FileItem.cs` 中新增的加密字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `EncryptionVersion` | `ushort` | 加密格式版本，当前为 1 |
| `KeyVersion` | `ushort` | 密钥版本号，支持历史密钥解密 |
| `DiskFileName` | `string` | 磁盘上的加密文件名 = SHA256(fileId + keyPrefix).ToHex()[0..16] |
| `FileHash` | `string?` | SHA256 哈希，完整性校验和 ETag |
| `BlockSize` | `int` | 分块大小，默认 1,048,576 |

---

## 8. 已知问题

1. **LocalStorageStrategy 加密集成未完成**：`ReadAsync`/`WriteAsync` 中加密流包装代码被注释（TODO Phase 1.5），当前加密逻辑可能走其他代码路径（如 `FileApiController` 或 `KeyRotationService` 中直接使用加密流）
2. **WS 加密文件公开访问 tag mismatch**：老文件（7 月 11 日前上传）使用已丢失密钥加密，当前密钥无法解密 → 需用户重新上传。公开访问已通过 `FileDownloadService` 统一解密修复（见 [06-public-access.md](06-public-access.md)），此问题仅剩数据层老文件
3. **Seek 不支持反向**：`AesGcmDecryptStream.Seek` 只能前向跳块，反向跳转需从头重新定位，大文件场景可能影响性能

---

## 9. 关键类/文件

| 类/文件 | 路径 |
|---|---|
| `EncryptedFileConstants` | `FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs` |
| `AesGcmEncryptStream` | `FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs` |
| `AesGcmDecryptStream` | `FileUploadServer.Infrastructure/Encryption/AesGcmChunkedStream.cs` |
| `IKeyProvider` | `FileUploadServer.Core/Interfaces/IKeyProvider.cs` |
| `KeyProvider` | `FileUploadServer.Infrastructure/Encryption/KeyProvider.cs` |
| `LocalStorageStrategy` | `FileUploadServer.Web/Services/LocalStorageStrategy.cs` |
| `FileItem`（加密字段） | `FileUploadServer.Core/Entities/FileItem.cs` |

---

## 10. 关联文档

- [05-key-management.md](05-key-management.md) -- 密钥加载、KeySlotManager、密钥轮换
- [03-permission.md](03-permission.md) -- 权限过滤，与加密无关
- [06-public-access.md](06-public-access.md) -- 公共文件访问中加密文件解密失败的已知问题
- [01-architecture.md](01-architecture.md) -- DI 注册、中间件管线
