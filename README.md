# FileUploadServer

基于 ASP.NET Core 10.0 的带分级权限、透明加密和分布式存储的文件服务器。

## 功能特点

### 🔐 分级权限系统
- **Admin Key（管理密钥）**：管理所有文件，仅内网可申请
- **Temporary Key（临时密钥）**：仅能访问自有文件，公网可申请（需 IP 白名单），过期后文件自动清理

### 🔒 文件透明加密（Phase 1.5）
- AES-256-GCM 分块加密，文件落地即密文
- LUKS 风格多密钥槽，支持恢复口令
- 密钥轮换后台任务，支持历史密钥解密
- 对用户完全透明：上传自动加密，下载自动解密

### 🌐 公共访问路径（Phase 2）
- 配置特定路径前缀（如 `/public/*`），匿名用户无需 API Key 即可访问
- 多层限流保护（IP 维度 + 文件维度 + 并发数）
- IP 白名单/黑名单、ETag/304 缓存支持

### 🔗 分布式 WebSocket 存储（Phase 3）
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
| 数据库 | PostgreSQL（端口 5432） + SQLite（开发） |
| ORM | Entity Framework Core 10.0（Migration 模式） |
| 加密 | AES-256-GCM（分块，默认 1MB/块） |
| WebSocket | System.Net.WebSockets + 自定义二进制帧协议 |
| 部署 | dotnet publish --sc true 自包含发布 |

## 项目结构

```
FileUploadServer/
├── FileUploadServer.Core/          # 实体、接口、模型
│   ├── Entities/                   # FileItem, ApiKey, WsClient, FileLocation 等
│   ├── Interfaces/                 # IKeyProvider, IFileStorageClient, IMessageHandler 等
│   ├── Models/                     # PublicPathOptions, WsMessageTypes
│   └── Services/                   # PathMatcher
├── FileUploadServer.Infrastructure/ # 数据访问、加密实现
│   ├── Data/AppDbContext.cs
│   ├── Encryption/                 # AesGcmChunkedStream, KeyProvider, KeySlotManager
│   └── Repositories/
├── FileUploadServer.Web/           # Web API、中间件、后台服务
│   ├── Controllers/                # FileApi, Admin, WsClientAdmin
│   ├── Middleware/                  # ApiKeyAuth, PublicFile, WebSocketHandler
│   ├── MessageHandlers/            # Upload/Download/Delete/List/PingPong
│   └── Services/                   # WsConnectionManager, ClientRouter, KeyRotation 等
├── FileUploadServer.WsClient/      # WS 客户端 SDK（独立部署）
├── FileUploadServer.Tests/
├── doc/                            # 设计文档
│   ├── IMPLEMENTATION_PLAN.md
│   ├── IMPLEMENTATION_PLAN_NETWORK.md
│   ├── IMPLEMENTATION_PLAN_CLIENTS.md
│   ├── DISTRIBUTED_DEPLOYMENT.md   # 分布式部署指南
│   └── BUG_TRACKER.md              # 踩坑合集
└── complete_schema.sql             # PostgreSQL 完整建表脚本
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
psql -h localhost -U postgres -d fileupload -f complete_schema.sql

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

## API 端点

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
GET    /p/{path}               # 匿名访问公共文件
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
```

### 公网 API
```
POST   /api/public/keys        # 申请临时密钥（需 IP 白名单）
```

### 命令行工具
```bash
--encrypt-init            # 初始化加密系统
--recover                 # 通过恢复口令重建密钥
--encrypt-add-slot        # 添加恢复口令
--encrypt-remove-slot N   # 移除恢复口令
--export-plaintext DIR    # 批量解密导出
```

## 部署

- 单机部署：见上文"快速开始"
- 分布式部署：见 [doc/DISTRIBUTED_DEPLOYMENT.md](doc/DISTRIBUTED_DEPLOYMENT.md)
- 已有远程服务器（111.229.53.125:7000）可用 `/deploy-file-upload-server` 部署

## 配置参考

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=fileupload;Username=postgres"
  },
  "Storage": {
    "Mode": "Hybrid",
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
| [IMPLEMENTATION_PLAN.md](doc/IMPLEMENTATION_PLAN.md) | 详细实施计划（加密/公共路径/WS架构/路由） |
| [IMPLEMENTATION_PLAN_NETWORK.md](doc/IMPLEMENTATION_PLAN_NETWORK.md) | WebSocket 协议设计、帧格式、流量控制 |
| [IMPLEMENTATION_PLAN_CLIENTS.md](doc/IMPLEMENTATION_PLAN_CLIENTS.md) | WS 客户端 SDK 设计 |
| [DISTRIBUTED_DEPLOYMENT.md](doc/DISTRIBUTED_DEPLOYMENT.md) | 分布式部署指南 |
| [BUG_TRACKER.md](doc/BUG_TRACKER.md) | 踩坑合集 |

## 许可

MIT
