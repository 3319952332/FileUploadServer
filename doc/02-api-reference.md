# HTTP API 完整参考
> 用途：列出 FileUploadServer 全部 HTTP API 端点，含方法、路由、参数、响应和权限要求
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md)、[03-permission.md](03-permission.md)、[10-cli-tools.md](10-cli-tools.md)

## 目录
1. [鉴权体系](#1-鉴权体系)
2. [FileApiController — 文件 CRUD](#2-fileapicontroller--文件-crud)
3. [AdminController — 密钥管理](#3-admincontroller--密钥管理)
4. [AdminController — 公开文件管理](#4-admincontroller--公开文件管理)
5. [IpWhitelistController — IP 白名单](#5-ipwhitelistcontroller--ip-白名单)
6. [PublicKeysController — 公网申请临时密钥](#6-publickeyscontroller--公网申请临时密钥)
7. [WsClientAdminController — WS 客户端管理](#7-wsclientadmincontroller--ws-客户端管理)
8. [命令行工具](#8-命令行工具)

## 1. 鉴权体系

### 1.1 中间件管线

```
请求 → Swagger/StaticFiles → WebSocket → ApiKeyAuthMiddleware → 控制器
                                         ↑ 放过 /api/admin/*、/api/public/*、/p/*
```

`ApiKeyAuthMiddleware` (`FileUploadServer.Web/Middleware/ApiKeyAuthMiddleware.cs`) 保护 `/api/files` 路由，从 query string 或 form 中读取 `key` 参数，查 `ApiKeys` 表校验有效性。

以下路径**跳过中间件、自行鉴权**：
- `/api/admin/*` — 控制器内 `IsLocalRequest()` 检测（仅允许 127.0.0.1 和 ::1）
- `/api/public/*` — `PublicKeysController` 自行做 IP 白名单检查
- `/p/*` — 由 `PublicFileMiddleware` 处理（匿名访问，已启用，见 [06-public-access.md](06-public-access.md)）

### 1.2 密钥类型

| 类型 | 来源 | 权限范围 |
|------|------|----------|
| Admin | 仅 localhost 创建 | 可访问全部文件 |
| Temporary | 公网 `/api/public/keys` 申请（需 IP 白名单） | 仅可访问自己上传的文件 |

### 1.3 响应格式

- 成功：200/201/204 + JSON body（视端点而定）
- 401：`Unauthorized: missing key parameter` 或 `Unauthorized: invalid or expired key`
- 403：权限不足（非 Admin 访问他人文件、非 localhost 访问 Admin 端点、IP 不在白名单）
- 404：资源不存在
- 503：WS 存储节点离线

---

## 2. FileApiController — 文件 CRUD

**路由前缀**：`/api/files`（受 ApiKeyAuthMiddleware 保护）  
**源码**：`FileUploadServer.Web/Controllers/FileApiController.cs`

### 2.1 GET /api/files — 获取文件列表

- **方法**：`GetAll()`
- **参数**：无（通过 query string `?key=xxx` 传入密钥）
- **成功响应** `200`：`List<FileItem>` JSON 数组，按 `UploadedAt` 倒序
- **权限**：Admin 密钥返回全部文件；Temporary 密钥仅返回自己上传的文件
- **错误**：401（缺 key / key 无效或过期）

### 2.2 GET /api/files/{id} — 获取单个文件元数据

- **方法**：`GetById(int id)`
- **参数**：`id`（int，路径参数，必填）
- **成功响应** `200`：`FileItem` JSON 对象
- **权限**：需当前密钥有权访问该文件（Admin 可访问任意，Temporary 仅可访问自己的）
- **错误**：401 / 403（无权访问）/ 404（文件不存在）

### 2.3 POST /api/files — 上传文件

- **方法**：`Upload(IFormFile file, [FromForm] string? path)`
- **参数**：
  - `file`：multipart form 文件流（必填）
  - `path`：`[FromForm]` string，可选，服务器存储路径，以 `/` 开头。未指定时使用原始文件名存储在根目录
  - `key`：query string 或 form 字段（鉴权用）
- **路由逻辑**：根据 `path` 前缀匹配 WS 存储节点（通过 `IStorageStrategyFactory`）；若未匹配到 WS 节点则存储本地；若加密已启用则 AES-256-GCM 透明加密
- **成功响应** `201`：`FileItem` JSON，含分配的 `Id`
- **权限**：需有效的任意类型密钥
- **错误**：400（文件为空）/ 401

### 2.4 DELETE /api/files/{id} — 删除文件

- **方法**：`Delete(int id)`
- **参数**：`id`（int，路径参数，必填）
- **成功响应** `204 No Content`
- **权限**：需当前密钥有权访问该文件
- **行为**：删除数据库记录 + 物理文件（本地磁盘或 WS 远程节点）；若 WS 节点离线，数据库记录仍删除但远程文件可能残留
- **错误**：401 / 403 / 404

### 2.5 GET /api/files/download/{id} — 下载文件

- **方法**：`Download(int id)`
- **参数**：`id`（int，路径参数，必填）
- **成功响应** `200`：文件流（`Content-Type` 为文件原始 MIME；加密文件透明解密后流式返回）
- **权限**：需当前密钥有权访问该文件
- **特殊处理**：
  - WS 存储模式：从远程节点拉取
  - 加密文件：通过 `AesGcmDecryptStream` 流式解密
  - 非加密文件：直接流式读取
- **错误**：401 / 403 / 404 / 503（WS 节点离线）

---

## 3. AdminController — 密钥管理

**路由前缀**：`/api/admin/keys`（跳过 ApiKeyAuthMiddleware，控制器内 IsLocalRequest 鉴权）  
**源码**：`FileUploadServer.Web/Controllers/AdminController.cs`

### 3.1 GET /api/admin/keys — 列出全部 API 密钥

- **方法**：`ListKeys()`
- **成功响应** `200`：`List<ApiKey>` JSON 数组，按 `CreatedAt` 倒序
- **权限**：仅 localhost（127.0.0.1 / ::1）
- **错误**：403（非 localhost）

### 3.2 POST /api/admin/keys — 创建 API 密钥

- **方法**：`CreateKey(string description, int expireMinutes, string keyType)`
- **参数**：
  - `description`（`[FromQuery]` string，默认 `""`）
  - `expireMinutes`（`[FromQuery]` int，默认 1440 即 24 小时）
  - `keyType`（`[FromQuery]` string，`"Admin"` 或 `"Temporary"`，默认 `"Admin"`；非法值回退为 `"Admin"`）
- **成功响应** `201`：`ApiKey` JSON（含生成的 `Key` 字段，由 `Guid.NewGuid().ToString("N")` 生成）
- **权限**：仅 localhost
- **错误**：403

### 3.3 DELETE /api/admin/keys/{key} — 删除（软禁用）API 密钥

- **方法**：`DeleteKey(string key)`
- **参数**：`key`（string，路径参数，密钥值本身，非 Id）
- **成功响应** `204 No Content`
- **行为**：设置 `IsDeleted = true`（软删除，不会立即从数据库移除）
- **权限**：仅 localhost
- **错误**：403 / 404（密钥不存在）

### 3.4 DELETE /api/admin/keys/cleanup — 清理过期/已删除密钥

- **方法**：`CleanupExpired()`
- **成功响应** `200`：返回被清理的密钥数量（int）
- **行为**：硬删除 `IsDeleted == true` 或 `ExpiresAt < UtcNow` 的记录
- **权限**：仅 localhost
- **错误**：403

---

## 4. AdminController — 公开文件管理

**注意**：以下端点路由**不在** `/api/admin/keys` 下，而是定义在同一个 `AdminController` 类中，使用独立路由。  
**源码**：`FileUploadServer.Web/Controllers/AdminController.cs`

### 4.1 PUT /api/admin/files/{id}/public — 设置文件公开标记

- **方法**：`SetFilePublic(int id, SetPublicRequest request)`
- **参数**：
  - `id`（int，路径参数，必填）
  - body：`{ "isPublic": bool, "publicPath": "string|null" }`
    - `isPublic`：设为 `true` 公开，`false` 取消公开
    - `publicPath`：公开访问路径（如 `/shared/report.pdf`），仅 `isPublic=true` 时有效
- **成功响应** `200`：更新后的 `FileItem` JSON
- **权限**：需要有效的 API Key（由中间件鉴权），无 localhost 限制
- **错误**：401 / 404

### 4.2 GET /api/admin/files/public — 查询全部公开文件（localhost）

- **方法**：`GetPublicFiles(int page, int pageSize)`
- **参数**：
  - `page`（`[FromQuery]` int，默认 1）
  - `pageSize`（`[FromQuery]` int，默认 20）
- **成功响应** `200`：`List<FileItem>` JSON 数组，分页返回
- **权限**：仅 localhost
- **错误**：403

### 4.3 GET /api/admin/stats/public-access — 公开文件访问统计

- **方法**：`GetPublicAccessStats()`
- **成功响应** `200`：
  ```json
  { "totalCount": int, "totalSize": long, "files": [...] }
  ```
- **权限**：仅 localhost
- **错误**：403

### 4.4 GET /api/public/files — 公开文件列表（无需认证）

- **方法**：`GetPublicFileList()`
- **成功响应** `200`：匿名文件列表，每个元素含 `id`、`fileName`、`fileSize`、`contentType`、`publicPath`、`url`（`/p{publicPath}`）
- **权限**：无需认证，但需通过 IP 白名单/限流（由外部中间件控制）
- **注意**：`/p/` 公开访问已启用（中间件 + `FileDownloadService` 统一解密），此端点返回的 URL 可直接匿名访问

---

## 5. IpWhitelistController — IP 白名单

**路由前缀**：`/api/admin/whitelist`（仅 localhost）  
**源码**：`FileUploadServer.Web/Controllers/AdminController.cs`（与 AdminController 同文件，独立类）

### 5.1 GET /api/admin/whitelist — 列出所有 IP 白名单

- **方法**：`ListWhitelist()`
- **成功响应** `200`：`List<IpWhitelist>` JSON 数组
- **权限**：仅 localhost
- **错误**：403

### 5.2 POST /api/admin/whitelist — 添加 IP 到白名单

- **方法**：`AddToWhitelist(string ipAddress, string description)`
- **参数**：
  - `ipAddress`（`[FromQuery]` string，必填）
  - `description`（`[FromQuery]` string，默认 `""`）
- **成功响应** `201`：`IpWhitelist` JSON
- **权限**：仅 localhost
- **错误**：400（ipAddress 为空）/ 403

### 5.3 DELETE /api/admin/whitelist/{id} — 从白名单移除 IP

- **方法**：`RemoveFromWhitelist(int id)`
- **参数**：`id`（int，路径参数，必填）
- **成功响应** `204 No Content`
- **权限**：仅 localhost
- **错误**：403

---

## 6. PublicKeysController — 公网申请临时密钥

**路由前缀**：`/api/public/keys`（公网可访问，需 IP 白名单）  
**源码**：`FileUploadServer.Web/Controllers/AdminController.cs`

### 6.1 POST /api/public/keys — 申请临时密钥

- **方法**：`CreateTemporaryKey(string description, int expireMinutes)`
- **参数**：
  - `description`（`[FromQuery]` string，默认 `""`）
  - `expireMinutes`（`[FromQuery]` int，默认 60，最大 1440 即 24 小时）
- **成功响应** `201`：`ApiKey` JSON（`KeyType = "Temporary"`）
- **权限**：公网可访问，但调用者 IP 必须在白名单中
- **错误**：403（IP 不在白名单）

---

## 7. WsClientAdminController — WS 客户端管理

**路由前缀**：`/api/admin/ws-clients`（仅 localhost）  
**源码**：`FileUploadServer.Web/Controllers/WsClientAdminController.cs`  
**端点数**：6 个动作

### 7.1 GET /api/admin/ws-clients — 列出所有 WS 客户端

- **方法**：`GetAll()`
- **成功响应** `200`：客户端列表 JSON，每项含 `id`、`description`、`isEnabled`、`pathPrefixes`、`storageCapacity`、`currentStorage`、`lastConnectedAt`、`createdAt`、`isOnline`
- **权限**：仅 localhost
- **错误**：403

### 7.2 POST /api/admin/ws-clients — 注册新 WS 客户端

- **方法**：`Register(RegisterWsClientRequest request)`
- **请求体**：
  ```json
  { "description": "string (必填)", "pathPrefixes": ["string[]"], "storageCapacity": long }
  ```
- **成功响应** `200`：`{ "id": "string", "clientSecret": "string", "description": "string", "pathPrefixes": ["..."], "storageCapacity": long }`
  - **重要**：`clientSecret` 仅此一次返回，格式为 `sk-wsc-<64 hex chars>`
- **权限**：仅 localhost
- **错误**：400（`description` 为空）/ 403

### 7.3 DELETE /api/admin/ws-clients/{id} — 注销 WS 客户端

- **方法**：`Unregister(string id)`
- **参数**：`id`（string，路径参数，客户端 ID）
- **行为**：若在线则先断开连接，然后删除数据库记录
- **成功响应** `204 No Content`
- **权限**：仅 localhost
- **错误**：403 / 404

### 7.4 GET /api/admin/ws-clients/{id}/stats — 查看客户端状态和存储用量

- **方法**：`GetStats(string id)`
- **参数**：`id`（string，路径参数）
- **成功响应** `200`：详细状态 JSON
  ```json
  {
    "id": "string", "description": "string", "isEnabled": bool, "isOnline": bool,
    "connectedAt": "datetime?", "lastHeartbeat": "datetime?", "lastConnectedAt": "datetime?",
    "storage": { "capacity": long, "used": long, "available": long, "usagePercent": double },
    "fileCount": int, "pathPrefixes": ["string[]"], "createdAt": "datetime"
  }
  ```
- **权限**：仅 localhost
- **错误**：403 / 404

### 7.5 POST /api/admin/ws-clients/{id}/regenerate-secret — 重新生成客户端密钥

- **方法**：`RegenerateSecret(string id)`
- **参数**：`id`（string，路径参数）
- **成功响应** `200`：`{ "id": "string", "clientSecret": "string" }`（新密钥仅此时返回）
- **权限**：仅 localhost
- **错误**：403 / 404

### 7.6 PATCH /api/admin/ws-clients/{id}/status — 启用/禁用 WS 客户端

- **方法**：`SetStatus(string id, SetClientStatusRequest request)`
- **参数**：
  - `id`（string，路径参数）
  - body：`{ "isEnabled": bool }`
- **行为**：若设为 `false`（禁用），同时断开当前连接
- **成功响应** `200`：`{ "id": "string", "isEnabled": bool }`
- **权限**：仅 localhost
- **错误**：403 / 404

---

## 8. 命令行工具

FileUploadServer 支持 CLI 命令（`--encrypt-init`、`--recover`、`--encrypt-add-slot`、`--encrypt-remove-slot`、`--export-plaintext`），详见 [10-cli-tools.md](10-cli-tools.md)。

---

## 端点总览

| # | HTTP 方法 | 路由 | 控制器 | 权限 |
|---|-----------|------|--------|------|
| 1 | GET | `/api/files` | FileApiController | API Key |
| 2 | GET | `/api/files/{id}` | FileApiController | API Key |
| 3 | POST | `/api/files` | FileApiController | API Key |
| 4 | DELETE | `/api/files/{id}` | FileApiController | API Key |
| 5 | GET | `/api/files/download/{id}` | FileApiController | API Key |
| 6 | GET | `/api/admin/keys` | AdminController | localhost |
| 7 | POST | `/api/admin/keys` | AdminController | localhost |
| 8 | DELETE | `/api/admin/keys/{key}` | AdminController | localhost |
| 9 | DELETE | `/api/admin/keys/cleanup` | AdminController | localhost |
| 10 | PUT | `/api/admin/files/{id}/public` | AdminController | API Key |
| 11 | GET | `/api/admin/files/public` | AdminController | localhost |
| 12 | GET | `/api/admin/stats/public-access` | AdminController | localhost |
| 13 | GET | `/api/public/files` | AdminController | 无需认证 |
| 14 | GET | `/api/admin/whitelist` | IpWhitelistController | localhost |
| 15 | POST | `/api/admin/whitelist` | IpWhitelistController | localhost |
| 16 | DELETE | `/api/admin/whitelist/{id}` | IpWhitelistController | localhost |
| 17 | POST | `/api/public/keys` | PublicKeysController | IP 白名单 |
| 18 | GET | `/api/admin/ws-clients` | WsClientAdminController | localhost |
| 19 | POST | `/api/admin/ws-clients` | WsClientAdminController | localhost |
| 20 | DELETE | `/api/admin/ws-clients/{id}` | WsClientAdminController | localhost |
| 21 | GET | `/api/admin/ws-clients/{id}/stats` | WsClientAdminController | localhost |
| 22 | POST | `/api/admin/ws-clients/{id}/regenerate-secret` | WsClientAdminController | localhost |
| 23 | PATCH | `/api/admin/ws-clients/{id}/status` | WsClientAdminController | localhost |

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览与中间件管线
- [03-permission.md](03-permission.md) — 分级权限细案
- [07-ws-storage.md](07-ws-storage.md) — WS 分布式存储
- [10-cli-tools.md](10-cli-tools.md) — CLI 运维工具
- [11-deployment.md](11-deployment.md) — 部署运维指南
