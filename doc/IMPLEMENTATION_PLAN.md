# FileUploadServer 详细实施计划

**创建时间**：2026-07-09  
**版本**：v2.0  
**状态**：规划完成，待评审  
**基于**：ROADMAP_NEXT.md v1.0

---

## 📋 目录

1. [概述](#概述)
2. [Phase 1.5：文件存储加密](#phase-15文件存储加密)
3. [Phase 2：公共访问路径](#phase-2公共访问路径)
4. [Phase 3：WebSocket 客户端架构](#phase-3websocket-客户端架构)
5. [Phase 4：多客户端路由与高级功能](#phase-4多客户端路由与高级功能)
6. [实施序列与依赖关系](#实施序列与依赖关系)
7. [新增/修改文件清单](#新增修改文件清单)
8. [风险与应对](#风险与应对)

---

## 概述

本文档对 ROADMAP_NEXT.md 中的三大功能进行详细的技术实施规划，从**服务端修改**、**客户端设计**、**网络层搭建**三个维度展开，确保各模块独立可并行开发。

### 项目现状速览

| 维度 | 当前状态 |
|------|----------|
| 架构 | 三层架构：Core / Infrastructure / Web |
| 框架 | ASP.NET Core 10.0, EF Core 10.0 |
| 数据库 | PostgreSQL（主）+ SQLite（开发） |
| 文件存储 | `wwwroot/uploads/`，GUID命名，明文存储 |
| 认证 | API Key（查询参数/表单），自定义中间件 |
| 现有 WebSocket | ❌ 无 |
| 现有加密 | ❌ 无 |
| 测试覆盖 | ❌ 仅有占位测试文件 |

### 三大功能依赖关系

```
Phase 1.5: 加密 ──→ 独立于其他功能，可最先实施
                        │
Phase 2: 公共路径 ──→ 依赖加密？→ 否。公共路径是权限逻辑，与存储介质无关
                        │
Phase 3: WS架构 ────→ 依赖加密？→ 否。WS转发的是字节流，加密对网关透明
                        │
Phase 4: 路由 ─────→ 依赖 Phase 3
```

**结论**：三大功能可**并行开发**，仅 Phase 4 依赖 Phase 3。

---

## Phase 1.5：文件存储加密

### 🎯 目标

文件落盘时使用 AES-256-GCM 分块加密，防止服务器磁盘数据泄露和恶意文件执行。

### 📐 详细设计

#### 1.1 加密架构总览

```
[用户请求] → 文件流 → [AesGcmEncryptStream] → [随机化磁盘文件名] → [加密文件]
                                      ↓
                              Master Key (32B)
                                      ↓
                            [KeySlotManager]
                          ┌──────┼──────┐
                     [Slot 0] [Slot 1] [Slot 2]
                     自动密钥  恢复口令1  恢复口令2
```

#### 1.2 文件格式

```
┌─────────────────────────────────────────────────────────────────┐
│ File Header (48 bytes, fixed)                                   │
├──────────┬─────────┬────────┬────────┬─────────────────────────┤
│ Magic    │ Version │ KeyVer │ BlkSize│ Reserved (36 bytes)     │
│ "FUEC"   │ 0x0001  │ uint16 │ uint32 │ 全零                     │
│ 4B       │ 2B      │ 2B     │ 4B     │ 36B                     │
├──────────┴─────────┴────────┴────────┴─────────────────────────┤
│ Chunk 0                                                         │
├──────────────┬────────────────────────────────┬────────────────┤
│ Nonce (12B)  │ Ciphertext (≤BlkSize bytes)    │ AuthTag (16B)  │
├──────────────┴────────────────────────────────┴────────────────┤
│ Chunk 1                                                         │
├──────────────┬────────────────────────────────┬────────────────┤
│ Nonce (12B)  │ Ciphertext (≤BlkSize bytes)    │ AuthTag (16B)  │
├──────────────┴────────────────────────────────┴────────────────┤
│ ...                                                             │
└─────────────────────────────────────────────────────────────────┘
```

- **Magic**: `0x46554543`（"FUEC" = FileUpload Encrypted）
- **Version**: `0x0001`（当前格式版本）
- **KeyVer**: 密钥版本号，支持密钥轮换
- **BlkSize**: 每块明文大小，默认 `1048576`（1MB）
- **Nonce**: 每块独立随机 12 字节，GCM 模式安全性要求 Nonce 唯一
- **AuthTag**: GCM 认证标签，保证完整性

#### 1.3 加密/解密流实现

**AesGcmEncryptStream** — 写入时透明加密：

```
Write(buffer, offset, count):
  ├─ 将数据写入内部明文缓冲区
  ├─ 当缓冲区 ≥ BlockSize 时：
  │   ├─ 生成 12 字节随机 Nonce（RandomNumberGenerator）
  │   ├─ 使用 AES-256-GCM 加密缓冲区前 BlockSize 字节
  │   ├─ 写入 [Nonce 12B][Ciphertext][AuthTag 16B] 到内部流
  │   └─ 移除已处理的明文
  └─ 返回

Flush() / Dispose():
  └─ 处理剩余的明文数据（最后一块）
     ├─ 生成随机 Nonce
     ├─ 加密剩余数据
     ├─ 写入 [Nonce][Ciphertext][AuthTag]
     └─ 写入 File Header（如果尚未写入）
```

**AesGcmDecryptStream** — 读取时透明解密：

```
Read(buffer, offset, count):
  ├─ 读取并解析 File Header（首次）
  ├─ 从内部流读取 [Nonce 12B][Ciphertext][AuthTag 16B]
  ├─ 使用对应 KeyVer 的 Master Key 解密
  ├─ 返回明文到用户缓冲区
  └─ 支持 Seek：按块索引定位
```

#### 1.4 密钥管理

**KeyProvider** — 密钥加载与生命周期：

```csharp
// 接口定义（Core/Interfaces）
public interface IKeyProvider
{
    byte[] GetMasterKey(ushort keyVersion = 1);
    ushort CurrentKeyVersion { get; }
    bool SupportsKeyVersion(ushort version);
}
```

**密钥加载优先级**：
1. 环境变量 `FILE_ENCRYPTION_KEY`（base64, 32 字节）
2. 密钥文件 `Encryption:KeyFilePath`（默认 `/etc/fileuploadserver/encryption.key`）
3. 配置项 `Encryption:MasterKey`（仅开发环境）
4. 首次启动自动生成密钥文件

**KeySlotManager** — 密钥槽管理（LUKS 风格）：

```
密钥文件 (encryption.key):
  ┌─────────────────────────────────────────────┐
  │ {                                             │
  │   "version": 1,                               │
  │   "created": "2026-07-09T00:00:00Z",          │
  │   "slots": [                                   │
  │     {  Slot 0: auto  - 加密的Master Key  },    │
  │     {  Slot 1: passphrase - 恢复口令1  },      │
  │     {  Slot 2: passphrase - 离线备份  }        │
  │   ]                                            │
  │ }                                             │
  └─────────────────────────────────────────────┘
```

每个 Slot 包含：
- `encryptedMasterKey` — 用口令派生的密钥加密后的 Master Key
- `salt` — PBKDF2 盐值
- `iterations` — PBKDF2 迭代次数（默认 600000）
- `iv`, `tag` — GCM 参数
- `hint` — 口令提示（仅 passphrase 类型）

#### 1.5 文件名随机化

```
diskFileName = SHA256(fileId + masterKeyPrefix)[0..32].ToHex()
            = "a3f5b2c8d1e4f6a7b8c9d0e1f2a3b4c5"

磁盘路径 = uploads/a3/a3f5b2c8d1e4f6a7b8c9d0e1f2a3b4c5
          (前2字符作为子目录)
```

- 原始文件名仅存储数据库，磁盘上无扩展名
- 无法通过文件名判断文件类型

#### 1.6 数据库变更

```sql
ALTER TABLE "Files" ADD COLUMN "EncryptionVersion" smallint NOT NULL DEFAULT 1;
ALTER TABLE "Files" ADD COLUMN "KeyVersion" smallint NOT NULL DEFAULT 1;
ALTER TABLE "Files" ADD COLUMN "DiskFileName" varchar(64) NOT NULL DEFAULT '';
ALTER TABLE "Files" ADD COLUMN "FileHash" varchar(64);
ALTER TABLE "Files" ADD COLUMN "BlockSize" integer NOT NULL DEFAULT 1048576;

CREATE INDEX idx_files_disk_name ON "Files"("DiskFileName");
```

#### 1.7 透明解密保证

加密对用户完全透明：

| 场景 | 行为 | 用户感知 |
|------|------|----------|
| 上传 | 加密后存储 | 无变化 |
| 下载 | 流式解密后返回 | 原始文件 |
| 预览 | 解密后流式输出 | 正常渲染 |
| 删除 | 删除加密文件 | 无变化 |

**实现要点**：
- `FileApiController.Download` 返回 `File(decryptStream, contentType, fileName)`
- ASP.NET Core 自动流式输出，大文件不占用内存
- Content-Type / Content-Disposition 等响应头从数据库读取

#### 1.8 密钥轮换后台服务

```csharp
// KeyRotationService : BackgroundService
// 执行间隔：可配置（默认 24 小时）
// 轮换步骤：
//   1. 生成新 Master Key（KeyVer + 1）
//   2. 用新密钥加密旧密钥槽的 Slot 0（自动切换）
//   3. 扫描旧密钥版本的文件
//   4. 逐个解密后用新密钥重新加密
//   5. 迁移速率控制（默认 100 个/分钟）
```

#### 1.9 命令行工具

| 命令 | 功能 | 交互方式 |
|------|------|----------|
| `--encrypt-init` | 初始化加密系统，生成 Master Key，设置恢复口令 | 交互式 |
| `--recover` | 通过恢复口令重建密钥文件 | 交互式 |
| `--encrypt-add-slot` | 添加新的恢复口令槽位 | 交互式 |
| `--encrypt-remove-slot` | 移除指定恢复口令槽位 | 参数 |
| `--export-plaintext` | 批量解密导出所有文件 | 参数 |

#### 1.10 旧文件迁移

迁移逻辑在启动时自动检测并执行：

```
1. 查找所有 EncryptionVersion = 0 或 DiskFileName 为空的文件
2. 对每个文件：
   a. 从 uploads/{storedFileName} 读取明文
   b. 生成随机 diskFileName
   c. 加密写入 uploads/{子目录}/{diskFileName}
   d. 更新 FileItem 记录
   e. 删除旧明文文件
3. 支持断点续传（记录已迁移的文件 ID）
4. 迁移完成前，下载时同时支持新旧两种路径
```

#### 1.11 单元测试要点

| 测试类别 | 测试用例 |
|----------|----------|
| 加密流 | 空文件、小文件（<1块）、大文件（多块）、1字节对齐、非对齐 |
| 解密流 | 正确密钥、错误密钥、损坏文件头、损坏密文、截断文件 |
| 密钥管理 | 密钥加载、密钥轮换、密钥版本切换 |
| 文件名 | 一致性验证（相同输入→相同输出）、唯一性 |
| 集成 | 上传→加密→下载→解密一致、大文件（100MB+）吞吐量 |
| 旧文件迁移 | 迁移前可读、迁移后可读、迁移后旧文件已删除 |
| 命令行 | --encrypt-init、--recover 全流程 |

---

## Phase 2：公共访问路径

### 🎯 目标

支持配置特定路径前缀，该路径下的文件无需 API Key 即可匿名访问。

### 📐 详细设计

#### 2.1 系统架构

```
[匿名用户] ── GET /p/public/doc.pdf ──→ [PublicFileMiddleware]
                                            ├─ 路径规范化 + 安全检查
                                            ├─ PathMatcher 匹配公共模式
                                            ├─ IP 白名单/黑名单检查
                                            ├─ 限流检查（IP + 文件维度）
                                            ├─ 查找 FileItem（is_public=true）
                                            ├─ 流式读取文件（解密透明）
                                            └─ 返回文件 + 缓存头
```

#### 2.2 配置模型

```csharp
public class PublicPathOptions
{
    public string[] Patterns { get; set; } = Array.Empty<string>();
    public long MaxFileSize { get; set; } = 52_428_800;  // 50MB
    public PublicRateLimitOptions RateLimit { get; set; } = new();
    public string CacheControl { get; set; } = "public,max-age=604800";
    public string[] AllowList { get; set; } = Array.Empty<string>();
    public string[] DenyList { get; set; } = Array.Empty<string>();
}

public class PublicRateLimitOptions
{
    public int PerIpPerMinute { get; set; } = 100;
    public int PerFilePerMinute { get; set; } = 20;
    public int ConcurrentDownloads { get; set; } = 50;
}
```

#### 2.3 路径匹配器 (PathMatcher)

```csharp
// 支持 glob 模式匹配
// /public/*      → 匹配 /public/abc.jpg，不匹配 /public/a/b.jpg
// /public/**     → 匹配 /public/a/b/c.jpg
// /assets/*.jpg  → 匹配 /assets/logo.jpg

public class PathMatcher
{
    public bool IsMatch(string path, string pattern);
    public bool MatchesAnyPublicPattern(string path);
}
```

**安全措施**：
- 路径规范化：移除 `..` 和 `.` 段
- 拒绝包含 `..` 的路径（防止路径遍历）
- 拒绝空路径和根路径
- 路径最大长度限制（如 2048 字符）

#### 2.4 访问端点

```
GET /p/{*filePath}
  例如: GET /p/public/documents/report.pdf
       GET /p/shared/images/logo.png
```

**端点放置位置**：
- 在 `ApiKeyAuthMiddleware` 之前或白名单路径中
- 请求路径以 `/p/` 开头 → 跳过 API Key 验证

#### 2.5 ApiKeyAuthMiddleware 修改

```csharp
// 增加跳过规则
var path = context.Request.Path.Value ?? "";
if (path.StartsWithSegments("/p/", StringComparison.OrdinalIgnoreCase))
{
    await _next(context);
    return;
}
```

#### 2.6 PublicFileMiddleware 完整流程

```
InvokeAsync(HttpContext context):
  │
  ├─ 1. 验证路径以 /p/ 开头
  │     └─ 否 → 调用 _next，返回
  │
  ├─ 2. 提取文件路径：path = "/p/public/a.jpg" → "public/a.jpg"
  │
  ├─ 3. 路径安全检查
  │     ├─ 规范化（Path.GetFullPath）
  │     ├─ 检查是否包含 ".."
  │     └─ 检查路径长度
  │
  ├─ 4. PathMatcher 匹配公共路径模式
  │     └─ 不匹配 → 404
  │
  ├─ 5. IP 白名单/黑名单检查
  │     └─ 被拒绝 → 403
  │
  ├─ 6. 限流检查（IP 维度 + 文件维度）
  │     └─ 超限 → 429 + Retry-After 头
  │
  ├─ 7. 查找 FileItem（Where IsPublic=true && PublicPath == filePath）
  │     └─ 未找到 → 404
  │
  ├─ 8. 检查文件大小限制
  │     └─ 超过 MaxFileSize → 413
  │
  ├─ 9. 打开文件流（解密流，如果加密启用）
  │
  ├─ 10. 设置响应头
  │      ├─ Content-Type: file.ContentType
  │      ├─ Content-Disposition: inline（预览）或 attachment
  │      ├─ Cache-Control: public, max-age=604800
  │      ├─ ETag: "file.FileHash"（如果有）
  │      └─ Last-Modified: file.UploadedAt
  │
  ├─ 11. 检查条件请求
  │      └─ If-None-Match 匹配 → 304
  │
  └─ 12. 流式返回文件内容
```

#### 2.7 限流实现

使用 ASP.NET Core 原生 `AddRateLimiter`：

```csharp
// Program.cs
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public-file-ip", opt =>
    {
        opt.PermitLimit = 100;               // 每 IP 每分钟 100 次
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });

    options.RejectionStatusCode = 429;
});

// 中间件中按路径应用策略
app.UseRateLimiter();
```

**额外限流层**（中间件内部）：
- `ConcurrentDictionary<string, RateBucket>` 按文件路径限流（每文件每分钟 20 次）
- `SemaphoreSlim` 控制并发下载数（默认 50）

#### 2.8 FileItem 实体变更

```csharp
// 新增字段
public bool IsPublic { get; set; }
public string? PublicPath { get; set; }   // 如 "/public/documents/report.pdf"
```

#### 2.9 管理 API

| 方法 | 路由 | 说明 | 鉴权 |
|------|------|------|------|
| `PUT` | `/api/admin/files/{id}/public` | 设置/取消公共访问 | localhost only |
| `GET` | `/api/admin/files/public` | 查询所有公共文件（分页） | localhost only |
| `GET` | `/api/admin/stats/public-access` | 公共文件访问统计 | localhost only |

**设置公共访问请求体**：
```json
{
    "isPublic": true,
    "publicPath": "/public/documents/report.pdf"
}
```

**验证**：
- `publicPath` 必须在已配置的公共路径前缀中
- `publicPath` 不能与其他文件的公共路径冲突

#### 2.10 单元测试要点

| 测试类别 | 测试用例 |
|----------|----------|
| 路径匹配 | 精确匹配、通配符匹配、多段匹配、不匹配 |
| 路径安全 | 路径遍历攻击（`../`）、空路径、超长路径 |
| 鉴权跳过 | `/p/` 路径不经过 API Key 验证 |
| 限流 | 单 IP 超限、多 IP 独立计数、限流窗口重置 |
| 缓存 | ETag 匹配返回 304、Cache-Control 头正确 |
| 管理 API | 设置公共、取消公共、分页查询 |

---

## Phase 3：WebSocket 客户端架构

### 🎯 目标

将"服务器存储"模式演进为"网关转发 + 客户端存储"模式，服务端仅负责认证、路由、转发、流量控制。

### 📐 详细设计

#### 3.1 系统架构总览

```
┌─────────────────────────────────────────────────────────────────────┐
│                        FileUploadServer (网关)                       │
│                                                                     │
│  ┌──────────┐  ┌──────────────┐  ┌──────────────────────────────┐  │
│  │ HTTP API  │  │ WS 连接管理   │  │ 转发引擎                     │  │
│  │ (现有)    │  │              │  │                              │  │
│  │ 上传/下载 │  │ WsConnection │  │ UploadForwarder              │  │
│  │ 删除/列表 │  │ Manager      │  │ DownloadForwarder            │  │
│  │          │  │              │  │ DeleteForwarder               │  │
│  └─────┬────┘  └──────┬───────┘  └──────────────┬───────────────┘  │
│        │              │                          │                   │
│        └──────────────┼──────────────────────────┘                   │
│                       │                                              │
│                ClientRouter                                         │
│           (路径前缀路由 + 负载均衡)                                   │
└───────────────────────┼──────────────────────────────────────────────┘
                        │ WebSocket (wss://)
                        ▼
┌─────────────────────────────────────────────────────────────────────┐
│  WS 客户端集群                                                        │
│                                                                     │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐              │
│  │ WS Client A  │  │ WS Client B  │  │ WS Client C  │              │
│  │ /public/*    │  │ /private/*   │  │ /archive/*   │              │
│  │ 本地磁盘存储  │  │ 对象存储 S3   │  │ NAS 存储      │              │
│  └──────────────┘  └──────────────┘  └──────────────┘              │
└─────────────────────────────────────────────────────────────────────┘
```

#### 3.2 新增加实体

**WsClient** — 注册的 WS 客户端：

```csharp
public class WsClient
{
    public string Id { get; set; } = string.Empty;           // 客户端唯一标识
    public string ClientSecretHash { get; set; } = string.Empty; // SHA256(secret)
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string PathPrefixes { get; set; } = string.Empty; // 逗号分隔的路径前缀
    public long StorageCapacity { get; set; } = -1;          // -1 = 无限制
    public long CurrentStorage { get; set; }
    public DateTime? LastConnectedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

**FileLocation** — 文件位置记录（新增表）：

```csharp
public class FileLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? FileHash { get; set; }
    public string ClientId { get; set; } = string.Empty;     // 所在的 WS 客户端
    public int ApiKeyId { get; set; }                         // 所属 API Key
    public bool IsPublic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}
```

#### 3.3 数据库新增表

```sql
CREATE TABLE "WsClients" (
    "Id" varchar(64) PRIMARY KEY,
    "ClientSecretHash" varchar(128) NOT NULL,
    "Description" varchar(255),
    "IsEnabled" boolean DEFAULT TRUE,
    "PathPrefixes" text DEFAULT '',
    "StorageCapacity" bigint DEFAULT -1,
    "CurrentStorage" bigint DEFAULT 0,
    "LastConnectedAt" timestamp,
    "CreatedAt" timestamp NOT NULL DEFAULT NOW()
);

CREATE TABLE "FileLocations" (
    "Id" uuid PRIMARY KEY,
    "FilePath" varchar(1024) NOT NULL,
    "FileName" varchar(255) NOT NULL,
    "FileSize" bigint NOT NULL,
    "FileHash" varchar(64),
    "ClientId" varchar(64) NOT NULL,
    "ApiKeyId" integer NOT NULL,
    "IsPublic" boolean DEFAULT FALSE,
    "CreatedAt" timestamp NOT NULL DEFAULT NOW(),
    "ExpiresAt" timestamp,
    UNIQUE ("FilePath", "ClientId")
);

CREATE INDEX idx_filelocations_path ON "FileLocations"("FilePath");
CREATE INDEX idx_filelocations_client ON "FileLocations"("ClientId");
CREATE INDEX idx_filelocations_public ON "FileLocations"("IsPublic");
```

#### 3.4 WebSocket 连接管理

**WsConnectionManager** — 连接池核心：

```csharp
public class WsConnectionManager
{
    // 内存状态
    private readonly ConcurrentDictionary<string, WsClientConnection> _connections = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _pathPrefixIndex = new();
    // 路径前缀 → 客户端 ID 集合（快速路由）

    // 核心方法
    Task<bool> RegisterConnection(string clientId, WebSocket ws, string[] pathPrefixes);
    Task UnregisterConnection(string clientId);
    WsClientConnection? GetConnection(string clientId);
    List<WsClientConnection> GetConnectionsForPath(string filePath);
    bool TryPickClientForPath(string filePath, out WsClientConnection? client);

    // 心跳检测
    void StartHeartbeatCheck();  // Timer 每 30s 检查
    void UpdateHeartbeat(string clientId);
}
```

**WsClientConnection** — 单连接状态：

```csharp
public class WsClientConnection
{
    public string ClientId { get; set; } = string.Empty;
    public WebSocket WebSocket { get; set; } = null!;
    public DateTime ConnectedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public long TotalStorageBytes { get; set; }
    public List<string> SupportedPaths { get; set; } = new();
    public CancellationTokenSource DisconnectCts { get; set; } = new();
}
```

#### 3.5 连接生命周期

```
[客户端]                              [服务端]
   │                                      │
   │── GET /ws/connect?clientId=X&token=T │
   │                                      │── 验证 clientId + token
   │                                      │── 升级 WebSocket
   │── WebSocket 连接建立                  │── 注册到连接池
   │                                      │
   │── JSON: {"type":"ping"}              │
   │                                      │── 更新心跳时间
   │── JSON: {"type":"pong"}              │
   │   每 30 秒                            │
   │                                      │── 60 秒无心跳
   │                                      │── 标记断开
   │                                      │── 触发 OnClientDisconnected
   │── 自动重连 (指数退避 1s→2s→...→30s)   │
```

#### 3.6 协议设计

**控制消息（JSON 文本帧）**：

```json
{
    "type": "upload_request | upload_ack | upload_complete | upload_error |
             download_request | download_data | download_complete | download_error |
             delete_request | delete_complete |
             list_request | list_response |
             ping | pong | error",
    "requestId": "uuid",
    "path": "/path/to/file",
    "fileName": "example.txt",
    "fileSize": 1024000,
    "chunkIndex": 0,
    "totalChunks": 5,
    "metadata": { ... },
    "errorMessage": "..."
}
```

**数据传输（二进制帧）**：
- 紧跟在 `upload_ack` 或 `download_data` 控制消息之后
- 纯文件二进制内容
- 分块传输，每块 64KB - 1MB

**消息处理器接口**：

```csharp
public interface IMessageHandler
{
    string MessageType { get; }  // 如 "upload_request"
    Task HandleAsync(string clientId, JsonDocument message,
                     byte[]? payload, WsClientConnection connection);
}
```

| 处理器 | 功能 |
|--------|------|
| `UploadRequestHandler` | 接收上传请求 → 返回 ACK → 接收二进制 → 存储 |
| `DownloadRequestHandler` | 读取文件 → 分块返回二进制 |
| `DeleteRequestHandler` | 删除文件 → 返回确认 |
| `ListRequestHandler` | 列出文件 → 返回 JSON |
| `PingPongHandler` | 心跳处理 |

#### 3.7 上传转发详细流程

```
用户 POST /api/upload?key=xxx&path=/public/abc.jpg
  │
  ├─ 1. ApiKeyAuthMiddleware 验证 API Key
  │
  ├─ 2. FileApiController.Upload:
  │     ├─ 验证路径合法性
  │     ├─ ClientRouter.TryPickClientForPath("/public/abc.jpg")
  │     │     └─ 根据路径前缀匹配客户端
  │     ├─ 生成 requestId (Guid)
  │     ├─ 创建转发上下文 (ForwardContext)
  │     │     ├─ requestId, clientId, path, fileName, fileSize
  │     │     ├─ CancellationToken (30s 超时)
  │     │     └─ 用户请求 Stream
  │     │
  │     ├─ 3. 发送 upload_request 到客户端
  │     │     WebSocket.SendAsync(JSON: {type, requestId, path, fileName, fileSize})
  │     │
  │     ├─ 4. 等待客户端 upload_ack (5s 超时)
  │     │     └─ 超时 → 故障转移到下一个可用客户端
  │     │
  │     ├─ 5. 分块转发用户数据
  │     │     ├─ 读取 64KB 用户请求流
  │     │     ├─ WebSocket.SendAsync(二进制帧, chunkIndex, totalChunks)
  │     │     └─ 循环直到流结束
  │     │
  │     ├─ 6. 等待客户端 upload_complete
  │     │     └─ 含 fileHash, fileSize
  │     │
  │     ├─ 7. 记录 FileLocation
  │     │     └─ clientId, path, fileHash, fileSize
  │     │
  │     └─ 8. 返回 201 Created 给用户
  │
  └─ 异常处理:
        ├─ WebSocket 断开 → 重试其他客户端
        ├─ 客户端返回 error → 返回 502 Bad Gateway
        └─ 超时 → 返回 504 Gateway Timeout
```

#### 3.8 下载转发详细流程

```
用户 GET /api/files/download/{fileId}?key=xxx
  │
  ├─ 1. 验证 API Key
  ├─ 2. 查询 FileItem + FileLocation
  ├─ 3. 获取 clientId
  ├─ 4. WsConnectionManager.GetConnection(clientId)
  │     └─ 断开 → 故障转移到同路径的其他客户端
  │
  ├─ 5. 发送 download_request
  │     WebSocket.SendAsync(JSON: {type, requestId, path})
  │
  ├─ 6. 接收 download_data（循环）
  │     ├─ 读取二进制帧
  │     ├─ 写入 HttpResponse.Body
  │     └─ 直到 download_complete
  │
  └─ 7. 完成响应
```

**流式转发的关键**：服务端不缓冲完整文件，而是用 `PipeReader/PipeWriter` 实现流式管道：

```
用户 Response.Body ← [PipeWriter] ← [PipeReader] ← WebSocket Receive
                                  ↑
                         服务端只是管道，不缓冲
```

#### 3.9 删除转发流程

```
用户 DELETE /api/files/{fileId}?key=xxx
  │
  ├─ 1. 验证 API Key + 权限
  ├─ 2. 查询 FileLocation
  ├─ 3. 发送 delete_request 到客户端
  ├─ 4. 等待 delete_complete
  ├─ 5. 删除 FileLocation 记录
  └─ 6. 返回 204 No Content
```

#### 3.10 客户端 SDK 接口

```csharp
// Core/Interfaces/IFileStorageClient.cs
public interface IFileStorageClient
{
    // 连接管理
    Task ConnectAsync(string serverUrl, string clientId, string clientSecret);
    Task DisconnectAsync();
    bool IsConnected { get; }
    event EventHandler<DisconnectEventArgs>? OnDisconnected;

    // 文件操作（由具体客户端实现）
    Task<Stream> ReadFileAsync(string path);
    Task WriteFileAsync(string path, Stream data);
    Task DeleteFileAsync(string path);
    Task<bool> FileExistsAsync(string path);
    Task<long> GetFileSizeAsync(string path);
    Task<string> GetFileHashAsync(string path);
}
```

**两种实现**：

| 实现类 | 位置 | 用途 |
|--------|------|------|
| `LocalFileStorageClient` | Web 层 | 本地存储降级模式、测试用 |
| `WsFileStorageClient` | 独立 NuGet 包 | 外部 WS 客户端，通过 WebSocket 连接到服务器 |

**WsFileStorageClient 内部**：
- 使用 `System.Net.WebSockets.ClientWebSocket`
- 自动心跳（30s Ping）
- 指数退避重连（1s→2s→4s→...→30s max）
- 请求-响应匹配（requestId → TaskCompletionSource）

#### 3.11 混合模式（向后兼容）

```csharp
public enum StorageMode { Local, WebSocket, Hybrid }

public interface IStorageStrategy
{
    Task<Stream> ReadAsync(string path);
    Task WriteAsync(string path, Stream data);
    Task DeleteAsync(string path);
}

public class StorageStrategyFactory
{
    public IStorageStrategy GetStrategy(string filePath)
    {
        // 1. 检查 storage_config 表（按路径模式）
        // 2. 无匹配 → 默认模式（配置的 Storage:Mode）
        // 3. WebSocket → WsStorageStrategy
        // 4. Local → LocalStorageStrategy
    }
}
```

配置示例：
```json
{
    "Storage": {
        "Mode": "Hybrid",
        "LocalPath": "wwwroot/uploads",
        "WebSocket": {
            "ReconnectIntervalMs": 1000,
            "MaxReconnectIntervalMs": 30000,
            "HeartbeatIntervalMs": 30000,
            "HeartbeatTimeoutMs": 60000,
            "ForwardTimeoutMs": 30000
        }
    }
}
```

#### 3.12 WS 客户端管理 API

| 方法 | 路由 | 说明 | 鉴权 |
|------|------|------|------|
| `GET` | `/api/admin/ws-clients` | 列出所有注册的 WS 客户端 | localhost |
| `POST` | `/api/admin/ws-clients` | 注册新客户端（生成 clientId+secret） | localhost |
| `DELETE` | `/api/admin/ws-clients/{id}` | 注销客户端 | localhost |
| `GET` | `/api/admin/ws-clients/{id}/stats` | 查看连接状态和存储用量 | localhost |

**注册客户端请求体**：
```json
{
    "description": "主存储节点 A",
    "pathPrefixes": ["/public/*", "/shared/*"],
    "storageCapacity": 1099511627776  // 1TB
}
```

**响应**（仅创建时返回 secret）：
```json
{
    "id": "client-node-a",
    "clientSecret": "sk-wsc-xxxxxxxxxxxx",
    "description": "主存储节点 A"
}
```

#### 3.13 单元测试要点

| 测试类别 | 测试用例 |
|----------|----------|
| 连接管理 | 注册连接、注销连接、重复注册、心跳超时 |
| 消息协议 | 控制消息序列化/反序列化、分块传输完整性 |
| 上传转发 | 小文件、大文件（多块）、空文件、断线重连 |
| 下载转发 | 正常下载、流式转发、客户端断线、超时 |
| 路由选择 | 路径前缀匹配、轮询均衡、无可用客户端 |
| 故障转移 | 主客户端断线→备客户端接管、重试次数耗尽 |
| 客户端 SDK | 连接、认证、心跳、重连、文件操作 |
| 混合模式 | Local vs WebSocket 切换、路径覆盖优先级 |

---

## Phase 4：多客户端路由与高级功能

### 🎯 目标

实现多客户端间的智能路由、负载均衡和故障转移。

### 📐 详细设计

#### 4.1 路由策略

```csharp
public enum RouteStrategy
{
    PathPrefix,      // 按路径前缀路由（最常用）
    RoundRobin,      // 同前缀下轮询
    LeastStorage,    // 选择存储用量最低的
    WeightedRandom   // 按容量加权随机
}
```

**ClientRouter** 核心逻辑：

```
SelectClient(filePath):
  │
  ├─ 1. 从 _pathPrefixIndex 找到匹配路径前缀的所有客户端
  │     └─ 无匹配 → null
  │
  ├─ 2. 过滤已断开、禁用的客户端
  │     └─ 全部不可用 → null
  │
  ├─ 3. 按策略选择最优客户端
  │     ├─ PathPrefix: 最精确匹配优先（/public/a/b > /public/a > /public）
  │     ├─ RoundRobin: 轮流选择
  │     ├─ LeastStorage: 选择 CurrentStorage 最小的
  │     └─ WeightedRandom: 按 StorageCapacity 加权随机
  │
  └─ 4. 返回选中的客户端
```

#### 4.2 故障转移机制

```
发送请求到 client-A:
  │
  ├─ 成功 → 完成
  │
  └─ 失败（断开/超时/错误）:
        ├─ 标记 client-A 为暂时不可用（cooldown 30s）
        ├─ 从同路径前缀的其他客户端中选择 client-B
        ├─ 重试请求（自动重发控制消息）
        ├─ 重试 2 次仍失败
        │     ├─ 上传：返回 502 Bad Gateway
        │     └─ 下载：尝试从其他客户端获取（数据可能已复制）
        └─ cooldown 结束后恢复 client-A
```

#### 4.3 连接健康度评分

```
HealthScore(client):
  = 基础分 100
  - (当前时间 - LastHeartbeat).TotalSeconds * 2    // 心跳延迟扣分
  - (CurrentStorage / StorageCapacity) * 30         // 存储使用率扣分
  + (IsEnabled ? 0 : -100)                          // 禁用直接最低分

选择策略：优先选健康度最高的客户端
```

#### 4.4 存储配置表

```sql
CREATE TABLE "StorageConfigs" (
    "Id" integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    "PathPattern" varchar(255) NOT NULL,     -- e.g. "/public/*"
    "StorageMode" varchar(20) NOT NULL DEFAULT 'Local',
    "ClientId" varchar(64),                  -- NULL = any client
    "Priority" integer DEFAULT 0,            -- 优先级，越高越优先匹配
    "CreatedAt" timestamp NOT NULL DEFAULT NOW()
);
```

---

## 实施序列与依赖关系

### 整体时间线

```
Week 1-3:  Phase 1.5 加密 ──────────────────────────────
               │
Week 2-4:          Phase 2 公共路径 ────────────────────
               │                      │
Week 4-7:                 Phase 3 WS架构 ───────────────
               │                      │              │
Week 7-8:                               Phase 4 路由 ──
```

### 详细任务分解

#### Phase 1.5：加密（建议优先级：最高）

| # | 任务 | 预计工时 | 前置 |
|---|------|----------|------|
| 1.1 | `AesGcmEncryptStream` / `AesGcmDecryptStream` 实现 | 2天 | 无 |
| 1.2 | `IKeyProvider` / `KeyProvider` 实现 | 1天 | 无 |
| 1.3 | `KeySlotManager` + 密钥文件格式 | 2天 | 无 |
| 1.4 | `FileItem` 实体变更 + 数据库迁移 | 0.5天 | 无 |
| 1.5 | 上传流程集成加密流 | 1天 | 1.1, 1.2, 1.4 |
| 1.6 | 下载流程集成解密流 | 1天 | 1.1, 1.2, 1.4 |
| 1.7 | 命令行工具（init/recover/add-slot/export） | 2天 | 1.3 |
| 1.8 | `KeyRotationService` 后台任务 | 1天 | 1.1, 1.2 |
| 1.9 | 旧文件迁移逻辑 | 1天 | 1.5, 1.6 |
| 1.10 | 单元测试 | 2天 | 全部 |
| 1.11 | 安全审计 | 0.5天 | 1.10 |

#### Phase 2：公共路径（建议优先级：中）

| # | 任务 | 预计工时 | 前置 |
|---|------|----------|------|
| 2.1 | `PublicPathOptions` 配置模型 + 绑定 | 0.5天 | 无 |
| 2.2 | `PathMatcher` 实现 | 0.5天 | 无 |
| 2.3 | `PublicFileMiddleware` 实现 | 1.5天 | 2.1, 2.2 |
| 2.4 | `ApiKeyAuthMiddleware` 修改（跳过 `/p/`） | 0.5天 | 无 |
| 2.5 | 限流集成（RateLimiter + 自定义限流） | 1天 | 2.3 |
| 2.6 | `FileItem` 公共字段 + 数据库迁移 | 0.5天 | 无 |
| 2.7 | 管理 API（设置/查询/统计） | 1天 | 2.6 |
| 2.8 | 缓存策略（ETag, Cache-Control, 304） | 0.5天 | 2.3 |
| 2.9 | 单元测试 | 1.5天 | 全部 |

#### Phase 3：WS 架构（建议优先级：中）

| # | 任务 | 预计工时 | 前置 |
|---|------|----------|------|
| 3.1 | `WsClient` / `FileLocation` 实体 + 数据库迁移 | 1天 | 无 |
| 3.2 | `WsConnectionManager` 连接池 | 2天 | 无 |
| 3.3 | WebSocket 升级中间件 + 客户端认证 | 1.5天 | 3.1, 3.2 |
| 3.4 | 心跳机制 + 断线检测 | 1天 | 3.2 |
| 3.5 | 协议定义 + 消息处理器框架 | 1天 | 无 |
| 3.6 | 上传转发实现 | 2天 | 3.2, 3.5 |
| 3.7 | 下载转发实现 | 1.5天 | 3.2, 3.5 |
| 3.8 | 删除转发实现 | 0.5天 | 3.2, 3.5 |
| 3.9 | `IFileStorageClient` SDK + `LocalFileStorageClient` | 1天 | 无 |
| 3.10 | WS 客户端管理 API | 1天 | 3.1 |
| 3.11 | `StorageStrategyFactory` + 混合模式 | 1.5天 | 3.6, 3.7, 3.8 |
| 3.12 | 单元测试 | 3天 | 全部 |

#### Phase 4：路由与高级功能（建议优先级：低）

| # | 任务 | 预计工时 | 前置 |
|---|------|----------|------|
| 4.1 | `ClientRouter` 多策略路由 | 2天 | 3.2 |
| 4.2 | 故障转移 + 重试机制 | 1.5天 | 4.1 |
| 4.3 | 健康度评分 + 自动降级 | 1天 | 4.1 |
| 4.4 | `StorageConfig` 表 + 管理 API | 1天 | 无 |
| 4.5 | 集成测试（多客户端场景） | 2天 | 全部 |

---

## 新增修改文件清单

### Core 层新增

| 文件路径 | 说明 |
|----------|------|
| `Core/Interfaces/IKeyProvider.cs` | 密钥提供者接口 |
| `Core/Interfaces/IFileStorageClient.cs` | 客户端存储接口 |
| `Core/Interfaces/IStorageStrategy.cs` | 存储策略接口 |
| `Core/Entities/WsClient.cs` | WebSocket 客户端实体 |
| `Core/Entities/FileLocation.cs` | 文件位置记录实体 |
| `Core/Models/PublicPathOptions.cs` | 公共路径配置模型 |
| `Core/Services/PathMatcher.cs` | 路径匹配逻辑 |

### Core 层修改

| 文件路径 | 修改内容 |
|----------|----------|
| `Core/Entities/FileItem.cs` | 新增 `IsPublic`, `PublicPath`, `EncryptionVersion`, `KeyVersion`, `DiskFileName`, `FileHash`, `BlockSize`, `StorageMode`, `ClientId`, `StoragePath` |

### Infrastructure 层新增

| 文件路径 | 说明 |
|----------|------|
| `Infrastructure/Encryption/AesGcmChunkedStream.cs` | 分块 GCM 加密/解密流 |
| `Infrastructure/Encryption/KeyProvider.cs` | 密钥提供者实现 |
| `Infrastructure/Encryption/KeySlotManager.cs` | 密钥槽管理器 |
| `Infrastructure/Repositories/FileLocationRepository.cs` | 文件位置仓储 |

### Infrastructure 层修改

| 文件路径 | 修改内容 |
|----------|----------|
| `Infrastructure/Data/AppDbContext.cs` | 新增 `DbSet<WsClient>`, `DbSet<FileLocation>`, `DbSet<StorageConfig>` |
| `Infrastructure/Repositories/FileItemRepository.cs` | 新增公共文件查询方法 |

### Web 层新增

| 文件路径 | 说明 |
|----------|------|
| `Web/Middleware/WebSocketHandlerMiddleware.cs` | WebSocket 升级与消息处理 |
| `Web/Middleware/PublicFileMiddleware.cs` | 公共文件访问 |
| `Web/Services/WsConnectionManager.cs` | 连接池管理器 |
| `Web/Services/WsClientAuthService.cs` | WS 客户端认证 |
| `Web/Services/ClientRouter.cs` | 多客户端路由 |
| `Web/Services/PublicFileRateLimiter.cs` | 公共文件限流 |
| `Web/Services/KeyRotationService.cs` | 密钥轮换后台任务 |
| `Web/Services/StorageStrategyFactory.cs` | 存储策略工厂 |
| `Web/Services/LocalStorageStrategy.cs` | 本地存储策略 |
| `Web/Services/WsStorageStrategy.cs` | WS 存储策略 |
| `Web/Controllers/WsClientAdminController.cs` | WS 客户端管理 API |
| `Web/Commands/EncryptionCommands.cs` | 加密 CLI 命令 |
| `Web/MessageHandlers/UploadRequestHandler.cs` | 上传请求处理 |
| `Web/MessageHandlers/DownloadRequestHandler.cs` | 下载请求处理 |
| `Web/MessageHandlers/DeleteRequestHandler.cs` | 删除请求处理 |
| `Web/MessageHandlers/ListRequestHandler.cs` | 列表请求处理 |
| `Web/MessageHandlers/PingPongHandler.cs` | 心跳处理 |

### Web 层修改

| 文件路径 | 修改内容 |
|----------|----------|
| `Web/Program.cs` | 注册新服务、中间件、限流、CLI 命令路由 |
| `Web/Middleware/ApiKeyAuthMiddleware.cs` | 跳过 `/p/` 公共路径 |
| `Web/Controllers/FileApiController.cs` | 集成加密流、WS 转发、混合存储策略 |
| `Web/Controllers/AdminController.cs` | 新增公共文件管理接口 |
| `Web/Services/BackgroundCleanupService.cs` | 新增清理 WS 客户端过期文件 |

---

## 风险与应对

| 风险 | 影响 | 概率 | 应对方案 |
|------|------|------|----------|
| AES-GCM 分块加密性能不达标 | 用户体验下降 | 低 | 使用 `BenchmarkDotNet` 预测试；支持配置块大小平衡性能 |
| 密钥文件丢失 | 数据不可恢复 | 中 | 多密钥槽机制；恢复口令离线备份；定期恢复演练 |
| WebSocket 连接不稳定 | 上传/下载失败 | 中 | 指数退避重连；故障转移；请求幂等性设计 |
| 大量并发转发导致服务端 OOM | 服务崩溃 | 中 | 流式处理（不缓冲完整文件）；信号量控制并发数 |
| 公共路径被滥用于 DDoS | 带宽耗尽 | 高 | 多层限流（IP+文件+并发）；CDN 前置部署 |
| 旧文件迁移过程中数据不一致 | 文件丢失 | 低 | 两阶段迁移（先复制后删除）；校验哈希 |
| 多客户端数据不一致 | 用户获取到旧版本 | 中 | 文件版本号；最终一致性设计；可选的强一致性路径 |

---

## 验收标准

### Phase 1.5 加密

- [ ] 磁盘上的文件以加密格式存储，无法直接读取
- [ ] 加密文件无法被直接执行
- [ ] 下载/预览返回解密后的原始数据，用户完全无感
- [ ] 多密钥槽机制可用，恢复口令可成功恢复 Master Key
- [ ] `--export-plaintext` 可批量导出明文文件
- [ ] 密钥轮换后新旧文件均可正常访问
- [ ] 旧文件可平滑迁移到加密存储
- [ ] 加密后性能损失 < 15%

### Phase 2 公共路径

- [ ] 公共路径文件无需 API Key 即可访问
- [ ] 路径遍历攻击被有效阻止
- [ ] 限流正常生效（IP 超限后 429）
- [ ] ETag 和 304 缓存正常工作
- [ ] 管理 API 可设置/取消公共标记

### Phase 3 WS 架构

- [ ] WS 客户端可稳定连接并维持心跳
- [ ] 上传通过 WS 客户端正确存储
- [ ] 下载通过 WS 客户端正确返回
- [ ] 删除操作正确同步到客户端
- [ ] 客户端断开后自动重连
- [ ] 原有 API 在新架构下继续正常工作
- [ ] 混合模式下 Local 和 WS 路径互不干扰

### Phase 4 路由

- [ ] 路径前缀路由正确分发到对应客户端
- [ ] 同一前缀多客户端轮询均衡
- [ ] 客户端故障后自动转移
- [ ] 健康度评分准确反映客户端状态

---

**文档创建人**：系统规划 (Claude Code Multi-Agent)  
**创建方式**：并行 Agent（代码结构探索 + 架构规划）→ 结果合成  
**下一次评审**：各 Phase 开发启动前
