# 限流与安全细案

> 用途：详细描述文件服务器的多层限流体系（并发/滑动窗口/内置固定窗口）和安全防护措施（路径遍历/文件名安全/IP 黑白名单/DDoS 防护），作为 [01-architecture.md](01-architecture.md) 中安全相关配置的深化补充。
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md) / [06-public-access.md](06-public-access.md) / [03-permission.md](03-permission.md)

## 目录

1. [限流体系概览](#限流体系概览)
2. [PublicFileRateLimiter 三层限流](#publicfileratelimiter-三层限流)
3. [ASP.NET Core 内置限流](#aspnet-core-内置限流)
4. [路径遍历防护](#路径遍历防护)
5. [文件名安全](#文件名安全)
6. [IP 白名单/黑名单](#ip-白名单黑名单)
7. [DDoS 防护组合策略](#ddos-防护组合策略)
8. [关键类/文件](#关键类文件)
9. [关联文档](#关联文档)

---

## 限流体系概览

文件服务器部署了**多层限流**防护，从应用层到框架层逐级收敛：

```
请求入口
   │
   ▼
┌─────────────────────────────────────────────────┐
│ 第 1 层：ASP.NET Core 内置限流                    │
│ AddRateLimiter — 固定窗口 "public-file-ip"       │
│ 100/min + 队列 10，超限返回 429                   │
└─────────────────────────────────────────────────┘
   │
   ▼
┌─────────────────────────────────────────────────┐
│ 第 2 层：PublicFileRateLimiter 应用层限流          │
│ ① 并发下载数 (SemaphoreSlim)                     │
│ ② IP 维度滑动窗口                                │
│ ③ 文件维度滑动窗口                               │
│ 仅对公共访问（/p/ 路径）生效                      │
└─────────────────────────────────────────────────┘
```

> 说明：第 2 层 PublicFileRateLimiter 由 `PublicFileMiddleware` 对 `/p/` 路径调用，中间件已启用，该层限流对公共访问生效。第 1 层 ASP.NET Core 内置限流始终生效。

---

## PublicFileRateLimiter 三层限流

来源：`FileUploadServer.Web/Services/PublicFileRateLimiter.cs`。

### 接口

```csharp
public interface IPublicFileRateLimiter
{
    (bool Allowed, int RetryAfterSeconds) TryAcquire(string ipAddress, string filePath);
    void Release();
}
```

### 三层检查流程

`TryAcquire(ipAddress, filePath)` 按顺序检查：

```
┌─────────────────────────────────────────────────────────┐
│ 第 1 层：并发下载数                                       │
│ SemaphoreSlim(_options.ConcurrentDownloads)             │
│ Wait(0) 零等待：满则返回 (false, 5)                       │
│ 注意：获取后需调用方 Release() 释放                        │
└─────────────┬───────────────────────────────────────────┘
              │ ✓ 通过
              ▼
┌─────────────────────────────────────────────────────────┐
│ 第 2 层：IP 维度滑动窗口                                  │
│ RateBucket(PerIpPerMinute, 1min)                        │
│ 按 ipAddress 分组                                       │
│ 超限返回 (false, 60)                                     │
└─────────────┬───────────────────────────────────────────┘
              │ ✓ 通过
              ▼
┌─────────────────────────────────────────────────────────┐
│ 第 3 层：文件维度滑动窗口                                 │
│ RateBucket(PerFilePerMinute, 1min)                      │
│ 按 filePath 分组                                        │
│ 超限返回 (false, 60)                                     │
└─────────────┬───────────────────────────────────────────┘
              │ ✓ 通过
              ▼
         (true, 0)
```

### RateBucket 滑动窗口实现

```csharp
// Queue<DateTime> 存储消费时间戳
// TryConsume():
//   1. 移除窗口外的过期时间戳 (Peek() < cutoff)
//   2. Count >= limit → 拒绝
//   3. Enqueue(now) → 允许
```

- 线程安全：通过 `lock(_lock)` 保护 `Queue<DateTime>` 操作
- 过期判定：`DateTime.UtcNow - _lastConsumedAt > _window * 2`（两倍窗口无活动）
- 清理：`CleanupExpiredBuckets()` 每 5 分钟清理一次过期桶

### 默认限流值（PublicRateLimitOptions）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `PerIpPerMinute` | 100 | 每 IP 每分钟最大请求数 |
| `PerFilePerMinute` | 20 | 每文件每分钟最大请求数 |
| `ConcurrentDownloads` | 50 | 最大并发下载数 |

来源：`FileUploadServer.Core/Models/PublicPathOptions.cs`。

### 并发满时重试等待

并发 `SemaphoreSlim` 满时返回 `RetryAfterSeconds = 5`（保守估算的等待时间），不在估算逻辑中循环等待。

### 异常安全

`TryAcquire` 内异常时自动释放已获取的 `SemaphoreSlim`，防止信号量泄漏。

---

## ASP.NET Core 内置限流

来源：`FileUploadServer.Web/Program.cs`。

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public-file-ip", opt =>
    {
        opt.PermitLimit = 100;          // 每窗口允许 100 个请求
        opt.Window = TimeSpan.FromMinutes(1); // 窗口大小 1 分钟
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;            // 队列最多排队 10 个请求
    });
    options.RejectionStatusCode = 429;  // 超限返回 HTTP 429
});

// 中间件应用
app.UseRateLimiter();  // 在 UseWebSockets 之前
```

- 策略名：`public-file-ip`（按 IP 分组，固定窗口 100 req/min）
- 队列处理：FIFO，最多排队 10 个请求（超过直接 429）
- 位置：管线第 5 位，在 ApiKeyAuthMiddleware 之前

---

## 路径遍历防护

### PathMatcher.ContainsPathTraversal

来源：`FileUploadServer.Core/Services/PathMatcher.cs`。

双重检查逻辑：

1. **分段检查**：将路径按 `/` 和 `\` 分段，拒绝任何 `"."` 和 `".."` 段
2. **字符串检查**：检查是否包含 `".."` 子串（但排除合法省略号 `"/..."` 和 `"\\..."`）

注意：此方法也拒绝单独的 `"."` 段（如 `/foo/./bar`），不仅是 `".."`。

### 路径长度限制

| 组件 | 限制 | 说明 |
|---|---|---|
| `PathMatcher` | 2048 字符 | `IsMatch` 入口检查 `path.Length > MaxPathLength` |
| `LocalFileStorageClient` (WsClient 端) | 1024 字符 | `GetSafePath` 中 `relativePath.Length > 1024` |
| `UploadRequestHandler` (网关) | 1024 字符 | `IsValidPath` 中 `path.Length > 1024` |
| `DownloadRequestHandler` (网关) | 1024 字符 | 同上 |
| `DeleteRequestHandler` (网关) | 1024 字符 | 同上 |

### 其他路径校验点

`LocalStorageStrategy.ResolveFilePath()`（Web 端）：
- 调用 `Path.GetFullPath()` 解析，然后检查结果是否在 `_basePath` 下
- 拒绝 `".."` 子串

`WsFileStorageClient.GetSafePath()`（WsClient 端）：
- 拒绝 `".."` 子串和 `\0` 空字符
- 拒绝不以 `/` 开头的路径

### 路径必须以 `/` 开头

`UploadRequestHandler.IsValidPath()` 等 handler 校验要求 `path.StartsWith('/')`，且不允许包含 `..` 和 `\0`。

---

## 文件名安全

### DiskFileName 随机哈希存储

来源：存储层使用随机生成的 `DiskFileName`（哈希风格）作为磁盘上的实际文件名，而非用户提供的原始文件名。

- 外部用户无法通过猜测文件名直接访问文件
- 文件类型信息不可从磁盘文件名推测
- 配置中 `Encryption:Enabled=true` 时，存储在类似 `uploads/ab/abcdef123...` 的子目录结构中

### 文件命名安全设计意图

- 原始文件名保留在 `FileItem.FileName` 字段（数据库元数据中）
- 磁盘上仅使用 `DiskFileName`
- 目录结构使用哈希前两字符作为子目录名（如 `uploads/{hash[0:2]}/{hash}`），避免单目录文件过多

---

## IP 白名单/黑名单

来源：`FileUploadServer.Core/Models/PublicPathOptions.cs`。

### 配置

```csharp
public class PublicPathOptions
{
    public string[] AllowList { get; set; } = [];  // 白名单（非空时仅允许列表内 IP）
    public string[] DenyList { get; set; } = [];   // 黑名单（拒绝列表内 IP）
}
```

### 规则

- **白名单非空**：仅允许 `AllowList` 中的 IP，其他全部拒绝
- **白名单为空**：不限制（所有 IP 允许）
- **黑名单**：`DenyList` 中的 IP 始终拒绝（优先级高于白名单）
- **通配符**：支持 `*` 通配符（如 `192.168.*.*`）

来源说明：IP 匹配逻辑由 `IIpWhitelistService`（`FileUploadServer.Infrastructure/Services/IpWhitelistService`）实现，`IpWhitelists` 数据库表存储持久化规则。

---

## DDoS 防护组合策略

多层防护的协同效果：

| 层面 | 机制 | 防御目标 |
|---|---|---|
| 传输层 | ASP.NET Core RateLimiter | 全局 IP 维度固定窗口限流 |
| 应用层 | PublicFileRateLimiter | 并发+IP+文件三维限流（/p/ 公共路径） |
| 应用层 | 路径遍历防护 | 目录遍历攻击 |
| 存储层 | DiskFileName 随机化 | 文件枚举猜测 |
| 鉴权层 | API Key 验证 | 未授权访问（非公共路径必须带 key） |
| 配置层 | IP 白名单/黑名单 | 未授权 IP 访问公共文件 |
| 请求层 | 请求体大小限制 (1GB) | 大文件 DDoS |
| 网络层 | HTTPS 重定向 + HSTS | 中间人攻击 |

### 配置建议

生产环境推荐组合：
- `PerIpPerMinute`：根据预期用户量调整（默认 100）
- `PerFilePerMinute`：热门文件保护（默认 20）
- `ConcurrentDownloads`：服务器带宽限制（默认 50）
- `AllowList`：内部网络可设置 CIDR 白名单
- 在反向代理（nginx/Caddy）层附加 `fail2ban` 或连接数限制

---

## 关键类/文件

| 文件 | 关键类 | 职责 |
|---|---|---|
| `FileUploadServer.Web/Services/PublicFileRateLimiter.cs` | `PublicFileRateLimiter`, `RateBucket` | 三层应用限流（并发+IP+文件滑动窗口） |
| `FileUploadServer.Web/Program.cs` | `AddRateLimiter` 配置块 | ASP.NET Core 内置固定窗口限流 |
| `FileUploadServer.Core/Services/PathMatcher.cs` | `PathMatcher` | Glob 路径匹配 + `ContainsPathTraversal` 安全校验 |
| `FileUploadServer.Web/Services/LocalStorageStrategy.cs` | `LocalStorageStrategy` | 本地存储 + `ResolveFilePath` 路径安全校验 |
| `FileUploadServer.WsClient/Storage/LocalFileStorageClient.cs` | `LocalFileStorageClient` | WS 节点本地存储 + `GetSafePath` 路径安全校验 |
| `FileUploadServer.WsClient/WsFileStorageClient.cs` | `WsFileStorageClient` | WS 客户端 + `GetSafePath` 路径安全校验 |
| `FileUploadServer.Core/Models/PublicPathOptions.cs` | `PublicPathOptions`, `PublicRateLimitOptions` | 公共访问+限流+IP黑白名单配置模型 |
| `FileUploadServer.Infrastructure/Services/IpWhitelistService.cs` | `IpWhitelistService` | IP 白名单/黑名单校验逻辑 |

---

## 关联文档

- [01-architecture.md](01-architecture.md) — 中间件管线（RateLimiter 位置）+ DI 注册清单
- [03-permission.md](03-permission.md) — API Key 权限体系
- [06-public-access.md](06-public-access.md) — 公共文件访问机制（`/p/` 路径，已启用）
