# 分级权限细案

> 用途：说明 API Key 的两种类型、鉴权流程、权限过滤规则、IP 白名单校验以及公网申请临时密钥的完整机制。
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md) / [02-api-reference.md](02-api-reference.md) / [04-encryption.md](04-encryption.md) / [06-public-access.md](06-public-access.md)

## 目录

1. [密钥类型](#密钥类型)
2. [ApiKey 实体与 IsValid()](#apikey-实体与-isvalid)
3. [鉴权流程：ApiKeyAuthMiddleware](#鉴权流程apikeyauthmiddleware)
4. [权限过滤：PermissionService](#权限过滤permissionservice)
5. [IP 白名单：IpWhitelistService](#ip-白名单ipwhitelistservice)
6. [公网申请临时密钥：PublicKeysController](#公网申请临时密钥publickeyscontroller)
7. [Admin 管理接口：AdminController](#admin-管理接口admincontroller)
8. [权限矩阵表](#权限矩阵表)
9. [关键类/文件](#关键类文件)
10. [关联文档](#关联文档)

---

## 1. 密钥类型

系统定义两种 API 密钥类型（`ApiKeyType` 枚举）：

| 类型 | 枚举值 | 字符串标识 | 说明 |
|---|---|---|---|
| **Admin** | `1` | `"Admin"` | 管理密钥，可访问所有文件，仅 localhost 可创建 |
| **Temporary** | `2` | `"Temporary"` | 临时密钥，仅访问自己上传的文件，公网可申请（需 IP 白名单），过期后文件被后台自动清理 |

> 源码：`FileUploadServer.Core/Entities/ApiKeyType.cs:6-17`。注意实际数据库中 `KeyType` 字段存储的是字符串（`"Admin"` / `"Temporary"`），而非枚举数值。

---

## 2. ApiKey 实体与 IsValid()

`ApiKey`（`Core/Entities/ApiKey.cs`）包含以下字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | `int` | 主键，自动递增 |
| `Key` | `string` | 密钥值，`Guid.NewGuid().ToString("N")` 生成 32 位 hex |
| `Description` | `string` | 密钥用途说明 |
| `CreatedAt` | `DateTime` | 创建时间 (UTC) |
| `ExpiresAt` | `DateTime` | 过期时间 (UTC) |
| `IsDeleted` | `bool` | 是否已手动删除（软删除标记） |
| `KeyType` | `string` | `"Admin"` 或 `"Temporary"` |

### IsValid() 逻辑

```csharp
public bool IsValid()
{
    return !IsDeleted && DateTime.UtcNow < ExpiresAt;
}
```

两个条件同时满足才有效：未被软删除、尚未过期。注意这里是**软删除**（`IsDeleted` flag），而非物理删除。后台清理服务（`BackgroundCleanupService`）负责物理删除已过期/已软删除的密钥。

> 源码：`FileUploadServer.Core/Entities/ApiKey.cs:46-49`

---

## 3. 鉴权流程：ApiKeyAuthMiddleware

### 3.1 跳过鉴权的路径

中间件首先检查请求路径，以下三类路径**直接跳过鉴权**（调用 `_next(context)`）：

| 路径前缀 | 说明 |
|---|---|
| `/api/admin` | Admin 接口（内部通过 `IsLocalRequest()` 做 localhost 限制，见第 7 节） |
| `/api/public` | 公网接口（如申请临时密钥，见第 6 节） |
| `/p/` | 公共文件访问路径（由 `PublicFileMiddleware` 处理，匿名访问，见 [06-public-access.md](06-public-access.md)） |

```csharp
if (context.Request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
    context.Request.Path.StartsWithSegments("/api/public", StringComparison.OrdinalIgnoreCase) ||
    context.Request.Path.StartsWithSegments("/p/", StringComparison.OrdinalIgnoreCase))
{
    await _next(context);
    return;
}
```

> 已知问题（已修复）：`StartsWithSegments("/p/")` 曾对 `/p/public/...` 返回 false（见 DEV_LOG 踩坑记录），已于提交 `8faadd5` 改为 `StartsWithSegments("/p")` 修复。

### 3.2 Key 提取与校验

对于未跳过的路径（主要是 `/api/files/*`），按以下顺序提取 key：

1. **Query 参数**：`context.Request.Query["key"]`
2. **表单参数**：只有当 `HasFormContentType` 时才尝试 `context.Request.Form["key"]`

提取到 key 后，查询数据库：

```csharp
var apiKey = await dbContext.ApiKeys
    .FirstOrDefaultAsync(k => k.Key == key && !k.IsDeleted);
```

查询时已预过滤 `IsDeleted`。再通过 `apiKey.IsValid()` 二次确认未过期。

### 3.3 鉴权结果写入 HttpContext

鉴权通过后，将 `ApiKey` 对象写入 `HttpContext.Items["CurrentApiKey"]`，后续 Controller/Service 可从该键读取当前请求的密钥信息。

> 源码：`FileUploadServer.Web/Middleware/ApiKeyAuthMiddleware.cs:1-66`

---

## 4. 权限过滤：PermissionService

`PermissionService` 实现 `IPermissionService`，提供两个核心方法：

### 4.1 CanAccessFileAsync（单文件检查）

```csharp
public async Task<bool> CanAccessFileAsync(int fileId, ApiKey currentKey)
{
    if (currentKey.KeyType == "Admin") return true;          // Admin 看全部
    var file = await _dbContext.Files.FindAsync(fileId);
    if (file == null) return false;
    return file.ApiKeyId == currentKey.Id;                   // Temporary 仅看自己的
}
```

### 4.2 GetAccessibleFilesQuery（列表过滤）

```csharp
public IQueryable<FileItem> GetAccessibleFilesQuery(ApiKey currentKey, IQueryable<FileItem> allFiles)
{
    if (currentKey.KeyType == "Admin") return allFiles;      // Admin 看全部
    return allFiles.Where(f => f.ApiKeyId == currentKey.Id);  // Temporary 仅看自己的
}
```

过滤规则：**Admin 密钥返回全量，Temporary 密钥按 `FileItem.ApiKeyId` 过滤**。每个 `FileItem` 在上传时记录上传者使用的 `ApiKeyId`，Temporary 密钥只能看到自己上传的文件。

> 源码：`FileUploadServer.Infrastructure/Services/PermissionService.cs:1-51`

---

## 5. IP 白名单：IpWhitelistService

`IpWhitelistService` 实现 `IIpWhitelistService` 接口，用于限制公网申请临时密钥的 IP 来源。

### 5.1 数据模型

`IpWhitelist` 实体（数据库表 `IpWhitelists`）：

| 字段 | 说明 |
|---|---|
| `Id` | 主键 |
| `IpAddress` | IP 地址字符串 |
| `Description` | 说明 |
| `CreatedAt` | 添加时间 |
| `IsEnabled` | 是否启用（支持禁用而非删除） |

### 5.2 接口方法

| 方法 | 说明 |
|---|---|
| `IsIpAllowedAsync(string ipAddress)` | 检查 IP 是否存在且 `IsEnabled == true` |
| `GetAllAsync()` | 列表查询（按 `CreatedAt` 倒序） |
| `AddAsync(ipAddress, description)` | 添加或重新启用（如 IP 已存在则设置 `IsEnabled=true`） |
| `RemoveAsync(id)` | 物理删除白名单记录 |

### 5.3 校验逻辑

```csharp
public async Task<bool> IsIpAllowedAsync(string ipAddress)
{
    if (string.IsNullOrEmpty(ipAddress)) return false;
    return await _dbContext.IpWhitelists
        .AnyAsync(w => w.IpAddress == ipAddress && w.IsEnabled);
}
```

仅做精确匹配（不支持 CIDR/通配符），同时要求 `IsEnabled == true`。

> 源码：`FileUploadServer.Infrastructure/Services/IpWhitelistService.cs:1-103`

---

## 6. 公网申请临时密钥：PublicKeysController

路由：`POST /api/public/keys`（无需 API Key，中间件跳过该路径鉴权）。

### 6.1 处理流程

1. **获取客户端 IP**：`HttpContext.Connection.RemoteIpAddress`
2. **IP 白名单校验**：调用 `IpWhitelistService.IsIpAllowedAsync()`，不在白名单中返回 403
3. **过期时间限制**：`expireMinutes` 参数默认 60 分钟，最大值 1440 分钟（24 小时），超出范围自动回退为 60
4. **创建密钥**：`KeyType = "Temporary"`, `Key = Guid.NewGuid().ToString("N")`, `IsDeleted = false`
5. **返回**：201 Created + `ApiKey` 对象

```csharp
if (expireMinutes <= 0 || expireMinutes > 1440)
{
    expireMinutes = 60;
}
```

### 6.2 安全边界

| 保护层 | 说明 |
|---|---|
| IP 白名单 | 仅允许指定 IP 申请密钥 |
| 过期上限 | 最长 24 小时，防止永久密钥泄露 |
| 无跨权访问 | Temp 密钥只能访问自己上传的文件（由 `PermissionService` 保障） |
| 自动清理 | `BackgroundCleanupService` 定期删除过期文件 |

> 源码：`FileUploadServer.Web/Controllers/AdminController.cs:312-363`

---

## 7. Admin 管理接口：AdminController

路由前缀 `/api/admin/keys`，所有端点只允许 **localhost** 访问（`IsLocalRequest()` 检查 `IPAddress.IsLoopback`）。这些端点已被 `ApiKeyAuthMiddleware` 跳过鉴权（因为走的是 `/api/admin` 前缀），由 `IsLocalRequest()` 自身做安全控制。

### 7.1 密钥管理端点

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/admin/keys` | GET | 列出所有密钥（按 `CreatedAt` 倒序） |
| `/api/admin/keys` | POST | 创建密钥（参数：`description`、`expireMinutes` 默认 1440、`keyType` 默认 Admin） |
| `/api/admin/keys/{key}` | DELETE | 软删除密钥（`IsDeleted = true`） |
| `/api/admin/keys/cleanup` | DELETE | 物理清理已过期/已删除的密钥 |

### 7.2 文件公开标记端点

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/admin/files/{id}/public` | PUT | 设置/取消文件公开标记（`IsPublic` + `PublicPath`），**无需 localhost 限制**（已有 API Key 鉴权） |
| `/api/admin/files/public` | GET | 分页查询公开文件列表（localhost only） |
| `/api/public/files` | GET | 公开文件列表（无需认证，返回 `IsPublic=true` 且 `PublicPath` 非空的文件） |
| `/api/admin/stats/public-access` | GET | 公开文件统计（localhost only） |

### 7.3 IP 白名单管理端点（IpWhitelistController）

路由 `/api/admin/whitelist`（仅 localhost）：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/admin/whitelist` | GET | 列出所有白名单 |
| `/api/admin/whitelist` | POST | 添加 IP（参数：`ipAddress`、`description`） |
| `/api/admin/whitelist/{id}` | DELETE | 移除 IP |

> 源码：`FileUploadServer.Web/Controllers/AdminController.cs:1-307`

---

## 8. 权限矩阵表

| 操作 | Admin Key | Temporary Key | 无 Key | 说明 |
|---|---|---|---|---|
| 列出文件 (`GET /api/files`) | 全部文件 | 仅自己上传的 | 401 | `PermissionService.GetAccessibleFilesQuery` |
| 文件详情 (`GET /api/files/{id}`) | 全部文件 | 仅自己的 | 401 | `PermissionService.CanAccessFileAsync` |
| 上传文件 (`POST /api/files`) | 可以 | 可以 | 401 | 需要有效 Key |
| 下载文件 (`GET /api/files/download/{id}`) | 全部文件 | 仅自己的 | 401 | `PermissionService.CanAccessFileAsync` |
| 删除文件 (`DELETE /api/files/{id}`) | 全部文件 | 仅自己的 | 401 | `PermissionService.CanAccessFileAsync` |
| 设置文件公开 (`PUT /api/admin/files/{id}/public`) | 可以 | 禁止 | 401 | 需要 Admin Key + API 鉴权 |
| 创建密钥 (`POST /api/admin/keys`) | — | — | — | localhost only，无需 Key |
| 公网申请 Temp Key (`POST /api/public/keys`) | — | — | 可以（需 IP 白名单） | 无需 Key，需 IP 白名单 |
| 公共文件访问 (`/p/*`) | — | — | 可以（无需 Key） | 中间件已启用，见 [06-public-access.md](06-public-access.md) |

---

## 9. 关键类/文件

| 类/文件 | 路径 |
|---|---|
| `ApiKey` 实体 | `FileUploadServer.Core/Entities/ApiKey.cs` |
| `ApiKeyType` 枚举 | `FileUploadServer.Core/Entities/ApiKeyType.cs` |
| `ApiKeyAuthMiddleware` | `FileUploadServer.Web/Middleware/ApiKeyAuthMiddleware.cs` |
| `PermissionService` | `FileUploadServer.Infrastructure/Services/PermissionService.cs` |
| `IPermissionService` | `FileUploadServer.Core/Interfaces/IPermissionService.cs` |
| `IpWhitelistService` | `FileUploadServer.Infrastructure/Services/IpWhitelistService.cs` |
| `PublicKeysController` | `FileUploadServer.Web/Controllers/AdminController.cs` (同文件，行 312+) |
| `AdminController` | `FileUploadServer.Web/Controllers/AdminController.cs` (行 1-224) |
| `IpWhitelistController` | `FileUploadServer.Web/Controllers/AdminController.cs` (行 229-307) |

---

## 10. 关联文档

- [01-architecture.md](01-architecture.md) -- 中间件管线、DI 注册清单
- [04-encryption.md](04-encryption.md) -- 透明加密实现，PermissionService 对加密无感知
- [06-public-access.md](06-public-access.md) -- `/p/` 路径的公共访问（已启用）
