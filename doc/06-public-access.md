# 公共访问细案

> 用途：说明公共文件访问路径 `/p/*` 的配置、中间件处理流程、三层限流机制，以及当前屏蔽状态与整改方向。
> 创建：2026-08-02 | 关联：[03-permission.md](03-permission.md) / [04-encryption.md](04-encryption.md) / [01-architecture.md](01-architecture.md)

## 目录

1. [⚠️ 重要现状：功能已屏蔽](#️-重要现状功能已屏蔽)
2. [PublicPathOptions：公共访问配置](#publicpathoptions公共访问配置)
3. [PublicFileMiddleware：12 步处理流程](#publicfilemiddleware12-步处理流程)
4. [三层限流：PublicFileRateLimiter](#三层限流publicfileratelimiter)
5. [WS 存储分支（Step 8.5）](#ws-存储分支step-85)
6. [本地磁盘分支（Step 9-12）](#本地磁盘分支step-9-12)
7. [屏蔽原因与整改方向](#屏蔽原因与整改方向)
8. [关键类/文件](#关键类文件)
9. [关联文档](#关联文档)

---

## 1. ✅ 现状：功能已修复并重新启用

**`PublicFileMiddleware` 已重新启用**（`Program.cs` 中 `app.UseMiddleware<PublicFileMiddleware>();`），通过共享 `FileDownloadService` 统一了「读取（WS/本地）+ 透明解密」逻辑，网页 / API / 公共访问三入口解密一致。

**修复内容**（提交 `8faadd5`）：
1. 删除中间件内 WS 直连分支（Step 8.5），统一走 `FileDownloadService`（支持 WS + 本地 + 解密）
2. 本地分支（Step 12）补上透明解密（修复返回密文问题）
3. 修复 `ApiKeyAuthMiddleware` 的 `/p/` 跳过 bug（`StartsWithSegments("/p/")` → `"/p"`）
4. 重新启用中间件

**测试**：`/p/public/hello.txt` 明文返回；新上传文件三入口返回相同明文。

> 历史遗留：部分老文件（7-11 及 8-02 上传）用已丢失密钥加密，当前密钥无法解密（tag mismatch），属数据层问题，需重新上传。

> 以下第 2-6 节描述 `PublicFileMiddleware` 的原始设计（12 步流程、限流等），中间件当前已启用，以下内容供实现细节参考。

---

## 2. PublicPathOptions：公共访问配置

定义在 `Core/Models/PublicPathOptions.cs`，配置节为 `"PublicPath"`。

### 2.1 配置项

| 配置项 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `Patterns` | `string[]` | `[]` | 公共路径模式列表，支持 `*`（单层通配）和 `**`（多层通配），如 `/public/*` |
| `MaxFileSize` | `long` | 52,428,800 (50MB) | 公共访问文件大小上限 |
| `RateLimit` | `PublicRateLimitOptions` | 见下 | 限流子配置 |
| `CacheControl` | `string` | `"public,max-age=604800"` | 响应 Cache-Control 头，默认缓存 7 天 |
| `AllowList` | `string[]` | `[]` | IP 白名单，非空时仅允许列表内 IP 访问，支持 `*` 通配符 |
| `DenyList` | `string[]` | `[]` | IP 黑名单，优先于白名单，支持 `*` 通配符 |

### 2.2 限流子配置（PublicRateLimitOptions）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `PerIpPerMinute` | 100 | 每 IP 每分钟最大请求数 |
| `PerFilePerMinute` | 20 | 每文件每分钟最大请求数 |
| `ConcurrentDownloads` | 50 | 最大并发下载数（全局 SemaphoreSlim） |

### 2.3 IP 匹配规则

`IsIpMatch()` 支持 `*` 通配符（如 `192.168.*` 匹配 `192.168.0.1`），使用预编译的正则表达式：

```csharp
// 正则模式缓存到 ConcurrentDictionary 中，避免重复编译
var regex = IpPatternCache.GetOrAdd(pattern, p =>
{
    var escaped = Regex.Escape(p).Replace("\\*", ".*");
    return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
});
```

**DenyList 优先于 AllowList**：先在 DenyList 中检查，命中则直接拒绝；然后检查 AllowList（非空时），不在 AllowList 中的也拒绝。

> 源码：`FileUploadServer.Core/Models/PublicPathOptions.cs:1-58`

---

## 3. PublicFileMiddleware：12 步处理流程

中间件代码保留在 `Web/Middleware/PublicFileMiddleware.cs`，以下为其原始 12 步处理流程（代码保留但当前不可达）：

### Step 1: 路径验证
检查请求路径是否以 `/p/` 开头。不是则调用 `_next(context)` 跳过。

### Step 2: 提取文件路径
移除 `/p/` 前缀，从 `/p/public/a.jpg` 提取为 `public/a.jpg`：
```csharp
var filePath = requestPath.AsSpan(3).ToString();
```

### Step 3: 路径安全检查（IsPathSafe）
- 拒绝空路径和根路径 `/`
- 拒绝包含 `..` 的路径遍历（排除合法的 `/.../` 省略号路径）
- 拒绝包含空字节 `\0`
- 路径最大长度限制 2048
- 拒绝以 `/` 或 `\` 开头的路径

### Step 4: PathMatcher 匹配
`PathMatcher.MatchesAnyPattern(filePath, opts.Patterns)` 检查文件路径是否匹配配置的公共路径模式（支持 `*` 和 `**` 通配）。

### Step 5: IP 白名单/黑名单检查
- 获取客户端真实 IP：优先 `X-Forwarded-For` 头（逗号分隔取第一个），回退 `RemoteIpAddress`
- `IsIpAllowed()`：先检查 DenyList，再检查 AllowList（非空时）

### Step 6: 限流检查
`rateLimiter.TryAcquire(remoteIp, filePath)` -- 三层限流（见第 4 节）。

### Step 7: 查找 FileItem
```csharp
var fileItem = await dbContext.Files
    .Where(f => f.IsPublic && f.PublicPath != null && f.PublicPath == filePath)
    .FirstOrDefaultAsync();
// 兼容前导斜杠: f.PublicPath == "/" + filePath
```

### Step 8: 检查文件大小限制
`fileItem.FileSize > opts.MaxFileSize` → 413 Request Entity Too Large。

### Step 8.5: WS 存储分支
如果 `StorageMode == "WebSocket"` → 进入 WS 读取流程（见第 5 节）。

### Step 9: 打开文件流（本地磁盘）
```csharp
var physicalPath = Path.Combine(uploadsPath, fileItem.StoredFileName);
```
文件不存在 → 404。

### Step 10: 设置响应头
- `Content-Type`: `fileItem.ContentType` 或默认 `application/octet-stream`
- `Content-Disposition`: 可预览类型（`text/`, `image/`, `audio/`, `video/`, `application/pdf`, `application/json`, `application/xml`）使用 `inline`，其余使用 `attachment`
- `Cache-Control`: `opts.CacheControl`（默认 `public,max-age=604800`）
- `ETag`: `SHA256(storedFileName-fileSize-uploadTicks)[0..8].hex`，格式 `"xxxxxxxxxxxxxxxx"`
- `Last-Modified`: `fileItem.UploadedAt.ToString("R")`
- `Content-Length`: `fileItem.FileSize`
- `X-Content-Type-Options`: `nosniff`

### Step 11: 条件请求检查
`If-None-Match` 与 ETag 匹配时返回 304 Not Modified（清除 Content-Type、Content-Disposition、Content-Length 头）。

### Step 12: 流式返回
`FileStream` 以 64KB 缓冲区 + `useAsync:true` 打开，`CopyToAsync(context.Response.Body)`。

> 源码：`FileUploadServer.Web/Middleware/PublicFileMiddleware.cs:49-354`

---

## 4. 三层限流：PublicFileRateLimiter

`PublicFileRateLimiter`（`Web/Services/PublicFileRateLimiter.cs`）实现 `IPublicFileRateLimiter` 接口，提供三层限流：

### 第一层：并发控制（SemaphoreSlim）
```csharp
if (!_concurrentSemaphore.Wait(0))  // 非阻塞尝试
    return (false, 5);              // 并发满，建议等待 5 秒
```
- 默认 `ConcurrentDownloads = 50`
- 成功获取后由调用方在 `finally` 中调用 `Release()` 释放

### 第二层：IP 维度滑动窗口
```csharp
var ipBucket = _ipBuckets.GetOrAdd(ipAddress, 
    _ => new RateBucket(PerIpPerMinute, TimeSpan.FromMinutes(1)));
```
- 默认每 IP 每分钟 100 次
- 使用 `Queue<DateTime>` 记录请求时间戳，窗口外自动过期

### 第三层：文件维度滑动窗口
```csharp
var fileBucket = _fileBuckets.GetOrAdd(filePath,
    _ => new RateBucket(PerFilePerMinute, TimeSpan.FromMinutes(1)));
```
- 默认每文件每分钟 20 次
- 防止单一热门文件被过度访问

### 清理机制

`CleanupExpiredBuckets()` 每 5 分钟清理一次过期的 IP 桶和文件桶（超过 2 倍窗口时间无活动），防止内存泄漏。

### RateBucket 实现细节

```csharp
// 滑动窗口：队首记录最老的时间戳
public bool TryConsume()
{
    var cutoff = now - _window;
    while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
        _timestamps.Dequeue();            // 移除过期时间戳
    if (_timestamps.Count >= _limit)
        return false;                     // 窗口内请求数超限
    _timestamps.Enqueue(now);             // 记录本次请求
    return true;
}
```

> 源码：`FileUploadServer.Web/Services/PublicFileRateLimiter.cs:1-201`

---

## 5. WS 存储分支（Step 8.5）

当 `FileItem.StorageMode == "WebSocket"` 时，中间件进入远程读取分支（代码保留在 `PublicFileMiddleware.cs:164-257`，当前不可达）：

1. 获取 `WsStorageStrategy` 和 `WsConnectionManager`
2. 检查 WS 客户端是否在线：`connectionManager.TryPickClientForPath(storagePath, out _)` -- 不在线返回 503
3. 通过 `strategy.ReadAsync(storagePath)` 读取文件流
4. 如果 `fileItem.EncryptionVersion > 0`：尝试用 `AesGcmDecryptStream` 解密
5. 将整个文件读取到内存（`MemoryStream`）
6. 设置响应头 + ETag + 条件请求检查（304）
7. `context.Response.Body.WriteAsync(wsData)`

### 已知问题

**WS 加密文件解密失败**：`AesGcmDecryptStream` 在 Step 8.5 中抛 `CryptographicException: authentication tag mismatch`。根因是部分老文件（7 月 11 日前上传）使用旧密钥加密，与当前 `KeyProvider` 的主密钥不匹配。

> 详见 [14-dev-log.md](14-dev-log.md) "公开访问排查" 章节与 [12-bug-tracker.md](12-bug-tracker.md)。

---

## 6. 本地磁盘分支（Step 9-12）

本地磁盘文件路径查找逻辑：

```csharp
var physicalPath = Path.Combine(uploadsPath, fileItem.StoredFileName);
```

仅使用 `StoredFileName` 字段（GUID 命名的文件名），不支持子目录格式。需要在 `uploadsPath`（默认为 `wwwroot/uploads`）下能找到该文件。

> 注意：`PublicFileMiddleware` 中 Step 12 的流式返回部分有加密流的 TODO 注释（`PublicFileMiddleware.cs:334-346`），标记 `EncryptionVersion > 0` 时应用 `AesGcmDecryptStream` 包装，但当前实现直接返回原始文件流（即返回密文，不对公共访问做解密）。

---

## 7. 修复记录（提交 8faadd5）

### 7.1 修复内容

| 原问题 | 修复方式 |
|---|---|
| WS 加密文件解密失败 / 本地分支返回密文 | 新建 `FileDownloadService` 统一「读取 + 解密」，中间件删除 WS 直连分支、统一调用 |
| 中间件直连 WS 节点违背分层架构 | 删除 Step 8.5 直连分支，改走共享 `FileDownloadService`（支持 WS + 本地） |
| `ApiKeyAuthMiddleware:27` `/p/` 跳过 bug | `StartsWithSegments("/p/")` → `StartsWithSegments("/p")` |

### 7.2 遗留问题

老文件（p.txt/d.txt/fresh.txt/Markdown入门.md、8-02 上传的 new_public.txt 与 8 张图片等）密文无法解密（密钥已丢失）→ 需用户提供原文件重新上传。

### 7.3 当前可用的公共文件查询

即使 `PublicFileMiddleware` 被屏蔽，以下端点仍可用：

| 端点 | 说明 |
|---|---|
| `GET /api/public/files` | 列出所有公开文件（无需认证） |
| `PUT /api/admin/files/{id}/public` | 设置/取消文件公开（需 Admin Key） |
| `GET /api/admin/files/public` | 分页查询公开文件（localhost） |
| `GET /api/admin/stats/public-access` | 公开文件统计（localhost） |

---

## 8. 关键类/文件

| 类/文件 | 路径 |
|---|---|
| `PublicPathOptions` | `FileUploadServer.Core/Models/PublicPathOptions.cs` |
| `PublicRateLimitOptions` | `FileUploadServer.Core/Models/PublicPathOptions.cs` |
| `PublicFileMiddleware` | `FileUploadServer.Web/Middleware/PublicFileMiddleware.cs` |
| `PublicFileRateLimiter` | `FileUploadServer.Web/Services/PublicFileRateLimiter.cs` |
| `IPublicFileRateLimiter` | `FileUploadServer.Web/Services/PublicFileRateLimiter.cs` |
| `PathMatcher` | `FileUploadServer.Core/Services/PathMatcher.cs` |
| `Program.cs`（屏蔽处） | `FileUploadServer.Web/Program.cs:188-198` |
| `ApiKeyAuthMiddleware`（`/p/` 跳过） | `FileUploadServer.Web/Middleware/ApiKeyAuthMiddleware.cs:27` |

---

## 9. 关联文档

- [03-permission.md](03-permission.md) -- API Key 鉴权流程，`/p/` 路径被中间件跳过鉴权
- [04-encryption.md](04-encryption.md) -- 加密文件解密原理，tag mismatch 根因分析
- [05-key-management.md](05-key-management.md) -- 密钥版本与历史密钥机制
- [01-architecture.md](01-architecture.md) -- 中间件管线顺序（PublicFileMiddleware 原应在 ApiKeyAuthMiddleware 之前）
- [14-dev-log.md](14-dev-log.md) -- 开发日志（公开访问排查记录）
- [12-bug-tracker.md](12-bug-tracker.md) -- 踩坑记录
