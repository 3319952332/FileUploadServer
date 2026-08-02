# FileUploadServer 项目总览

> 用途：项目定位、功能、技术栈、快速开始与配置的总览文档，进入项目先读这一篇。
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md) / [02-api-reference.md](02-api-reference.md) / [11-deployment.md](11-deployment.md)

基于 ASP.NET Core 10.0 的带分级权限、透明加密和分布式存储的文件服务器。

## 目录

1. [功能特点](#功能特点)
2. [技术栈](#技术栈)
3. [项目结构](#项目结构)
4. [快速开始（本地开发）](#快速开始本地开发)
5. [权限说明](#权限说明)
6. [API 端点概览](#api-端点概览)
7. [配置参考](#配置参考)
8. [文档索引](#文档索引)

---

## 功能特点

### 🔐 分级权限系统
- **Admin Key（管理密钥）**：管理所有文件，仅内网可申请
- **Temporary Key（临时密钥）**：仅能访问自有文件，公网可申请（需 IP 白名单），过期后文件自动清理

### 🔒 文件透明加密
- AES-256-GCM 分块加密，文件落地即密文
- LUKS 风格多密钥槽，支持恢复口令
- 密钥轮换后台任务，支持历史密钥解密
- 对用户完全透明：上传自动加密，下载自动解密

### 🌐 公共访问路径（当前已屏蔽，待整改）
- 配置特定路径前缀（如 `/public/*`），匿名用户无需 API Key 即可访问
- 多层限流保护（IP 维度 + 文件维度 + 并发数）
- IP 白名单/黑名单、ETag/304 缓存支持
- ⚠️ **2026-08-02 起已屏蔽**，WS 存储的加密文件经 `/p/` 访问解密失败，且违背"访问统一走 API"架构，详见 [06-public-access.md](06-public-access.md)

### 🔗 分布式 WebSocket 存储
- 网关负责认证/路由，WS 客户端负责实际文件存储
- 支持多客户端、多路径前缀路由
- 混合模式：无 WS 客户端时自动降级为本地存储
- 故障转移、健康度评分、指数退避重连

### 🛡️ 安全
- IP 白名单保护公网密钥申请
- 路径遍历攻击防护
- 限流防 DDoS
- 文件名随机哈希存储，无法推测文件类型

### 🕐 自动化运维
- 过期临时密钥 + 关联文件自动清理
- 加密密钥轮换
- WS 客户端过期文件清理

## 技术栈

| 组件 | 技术 |
|------|------|
| 后端框架 | ASP.NET Core 10.0 |
| 数据库 | PostgreSQL（端口 5432） + SQLite（仅开发禁用） |
| ORM | Entity Framework Core 10.0（Migration 模式） |
| 加密 | AES-256-GCM（分块，默认 1MB/块） |
| WebSocket | System.Net.WebSockets + 自定义二进制帧协议 |
| 部署 | dotnet publish --sc true 自包含发布 |

> ⚠️ 数据库**只用 PostgreSQL，绝不使用 SQLite**（详见 CLAUDE.md 关键规则）

## 项目结构

```
FileUploadServer/
├── CLAUDE.md                     # 项目导航索引 + 关键规则
├── doc/                          # 所有文档（编号 + 主题命名）
│   ├── 00-overview.md            # 本文件：项目总览
│   ├── 01-architecture.md        # 项目框架接口图
│   ├── 02-api-reference.md       # HTTP API 完整参考
│   ├── 03-permission.md          # 分级权限细案
│   ├── 04-encryption.md          # 文件加密细案
│   ├── 05-key-management.md      # 密钥生命周期细案
│   ├── 06-public-access.md       # 公共访问细案（已屏蔽）
│   ├── 07-ws-storage.md          # WS 分布式存储细案
│   ├── 08-mcp.md                 # MCP 接口细案
│   ├── 09-rate-limit.md          # 限流与安全细案
│   ├── 10-cli-tools.md           # CLI 运维工具细案
│   ├── 11-deployment.md          # 部署运维指南
│   ├── 12-bug-tracker.md         # 踩坑记录
│   ├── 13-mcp-baseline.md        # MCP 通用规范模板
│   └── 14-dev-log.md             # 开发日志
├── FileUploadServer.Core/        # 实体、接口、模型
│   ├── Entities/                 # FileItem, ApiKey, WsClient, FileLocation, IpWhitelist
│   ├── Interfaces/               # IKeyProvider, IFileStorageClient, IMessageHandler 等
│   ├── Models/                   # PublicPathOptions, WsMessageTypes（11 种消息）
│   └── Services/                 # PathMatcher
├── FileUploadServer.Infrastructure/ # 数据访问、加密实现
│   ├── Data/AppDbContext.cs
│   ├── Encryption/               # AesGcmChunkedStream, KeyProvider, KeySlotManager
│   └── Repositories/             # FileItemRepository, FileLocationRepository
├── FileUploadServer.Web/         # Web API 网关（认证 / 路由 / 元数据）
│   ├── Controllers/              # FileApi, Admin, WsClientAdmin
│   ├── Middleware/               # ApiKeyAuth, PublicFile(已屏蔽), WebSocketHandler
│   ├── MessageHandlers/          # Upload/Download/Delete/List/PingPong
│   └── Services/                 # WsConnectionManager, ClientRouter, KeyRotation 等
├── FileUploadServer.WsClient/    # WS 存储节点客户端（独立部署）
├── FileUploadServer.Mcp/         # MCP 接口（stdio JSON-RPC，6 个工具）
├── FileUploadServer.Tests/
└── sql/complete_schema.sql       # PostgreSQL 完整建表脚本
```

## 快速开始（本地开发）

### 环境准备

- .NET 10.0 SDK
- PostgreSQL 16+

### 启动

```bash
# 1. 创建数据库
psql -h localhost -U postgres -c "CREATE DATABASE fileupload"

# 2. 导入表结构
psql -h localhost -U postgres -d fileupload -f sql/complete_schema.sql

# 3. 配置连接（编辑 FileUploadServer.Web/appsettings.json）
# 或通过环境变量
ConnectionStrings__DefaultConnection="Host=localhost;Database=fileupload;Username=postgres;Password=xxx"

# 4. 运行
cd FileUploadServer.Web
dotnet run
```

访问 `http://localhost:5000/swagger` 查看 API 文档。

## 权限说明

| 操作 | Admin Key | Temporary Key |
|------|-----------|---------------|
| 文件列表 | 所有文件 | 仅自有文件 |
| 上传 | ✅ | ✅（自动关联） |
| 下载 | 所有文件 | 仅自有文件 |
| 删除 | 所有文件 | 仅自有文件 |
| 文件公开设置 | ✅ | ❌ |
| WS 客户端管理 | ✅ | ❌ |
| IP 白名单管理 | ✅ | ❌ |

详细逻辑见 [03-permission.md](03-permission.md)。

## API 端点概览

完整 23 个端点逐个体说明见 [02-api-reference.md](02-api-reference.md)。概览：

### 文件操作
```
POST   /api/files              # 上传文件（支持加密）
GET    /api/files              # 文件列表（按权限过滤）
GET    /api/files/{id}         # 文件详情
GET    /api/files/download/{id} # 下载文件（流式解密）
DELETE /api/files/{id}         # 删除文件
```

### 密钥管理（localhost only）
```
POST   /api/admin/keys         # 创建密钥
GET    /api/admin/keys         # 列出密钥
DELETE /api/admin/keys/{key}   # 删除密钥
DELETE /api/admin/keys/cleanup # 清理过期密钥
```

### 公共访问
```
GET    /p/{path}               # 匿名访问公共文件（中间件已屏蔽）
PUT    /api/admin/files/{id}/public  # 设置文件公开
GET    /api/admin/files/public       # 公共文件列表
GET    /api/admin/stats/public-access # 统计
```

### WS 客户端管理（localhost only）
```
POST   /api/admin/ws-clients           # 注册客户端
GET    /api/admin/ws-clients           # 列出客户端
DELETE /api/admin/ws-clients/{id}      # 注销客户端
GET    /api/admin/ws-clients/{id}/stats # 客户端状态
POST   /api/admin/ws-clients/{id}/regenerate-secret # 重生成密钥
PATCH  /api/admin/ws-clients/{id}/status  # 启用/禁用客户端
```

### 公网 API
```
POST   /api/public/keys        # 申请临时密钥（需 IP 白名单）
```

### 命令行工具
见 [10-cli-tools.md](10-cli-tools.md)：
```bash
--encrypt-init            # 初始化加密系统
--recover                 # 通过恢复口令重建密钥
--encrypt-add-slot        # 添加恢复口令
--encrypt-remove-slot N   # 移除恢复口令
--export-plaintext DIR    # 批量解密导出
```

## 配置参考

详细配置见 [11-deployment.md](11-deployment.md) 与各功能细案。

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=fileupload;Username=postgres"
  },
  "Storage": {
    "Mode": "Local",
    "LocalPath": "wwwroot/uploads"
  },
  "PublicPath": {
    "Patterns": ["/public/*"],
    "MaxFileSize": 52428800,
    "RateLimit": { "PerIpPerMinute": 100, "PerFilePerMinute": 20 }
  },
  "Encryption": {
    "KeyFilePath": "/etc/fileuploadserver/encryption.key"
  }
}
```

## 文档索引

| 文档 | 内容 |
|------|------|
| [01-architecture.md](01-architecture.md) | 项目框架接口图、分层、协议、接口总览 |
| [02-api-reference.md](02-api-reference.md) | HTTP API 完整参考 |
| [03-permission.md](03-permission.md) | 分级权限细案 |
| [04-encryption.md](04-encryption.md) | 文件加密细案 |
| [05-key-management.md](05-key-management.md) | 密钥生命周期细案 |
| [06-public-access.md](06-public-access.md) | 公共访问细案（已屏蔽待整改） |
| [07-ws-storage.md](07-ws-storage.md) | WS 分布式存储细案 |
| [08-mcp.md](08-mcp.md) | MCP 接口细案 |
| [09-rate-limit.md](09-rate-limit.md) | 限流与安全细案 |
| [10-cli-tools.md](10-cli-tools.md) | CLI 运维工具细案 |
| [11-deployment.md](11-deployment.md) | 部署运维指南 |
| [12-bug-tracker.md](12-bug-tracker.md) | 踩坑记录 |
| [13-mcp-baseline.md](13-mcp-baseline.md) | MCP 通用规范模板 |
| [14-dev-log.md](14-dev-log.md) | 开发日志 |

## 许可

MIT
