# FileUploadServer 分布式部署指南

**创建时间**：2026-07-10

---

## 目录

1. [架构概述](#架构概述)
2. [单机部署（默认）](#单机部署默认)
3. [网关 + WS 客户端分布式部署](#网关--ws-客户端分布式部署)
4. [加密配置](#加密配置)
5. [公共访问路径配置](#公共访问路径配置)
6. [高可用部署](#高可用部署)
7. [运维命令速查](#运维命令速查)

---

## 架构概述

FileUploadServer 支持三种存储模式，可根据规模灵活选择：

```
单机模式                        网关 + WS 客户端模式              高可用模式
┌──────────────────┐          ┌──────────────────┐          ┌──────────────────────┐
│ FileUploadServer │          │  网关服务器        │          │  LB (Nginx/HAProxy)  │
│ (API + 存储)      │          │  (仅 API/路由)     │      ┌──│                      │──┐
│                  │          │  Storage: Hybrid  │      │  └──────────────────────┘  │
│ PostgreSQL       │          └────────┬─────────┘      │                            │
│ 本地磁盘         │                   │                ▼                            ▼
└──────────────────┘          ┌────────┼─────────┐   ┌──────────────┐   ┌──────────────┐
                              ▼        ▼         ▼   │ 网关 Node 1  │   │ 网关 Node 2  │
                           ┌──────┐ ┌──────┐ ┌──────┐ │              │   │              │
                           │WS客户端│ │WS客户端│ │WS客户端│ │ PostgreSQL  │   │ PostgreSQL  │
                           │Node A │ │Node B │ │Node C │ │ (共享)      │   │ (共享)      │
                           │/public│ │/privt│ │/archv│ └──────┬───────┘   └──────┬───────┘
                           └──────┘ └──────┘ └──────┘        │                  │
                                                          ┌──┼──────────────────┼──┐
                                                          ▼  ▼                  ▼  ▼
                                                      ┌──────┐ ┌──────┐   ┌──────┐ ┌──────┐
                                                      │WS Cli│ │WS Cli│   │WS Cli│ │WS Cli│
                                                      │Node A│ │Node B│   │Node C│ │Node D│
                                                      └──────┘ └──────┘   └──────┘ └──────┘
```

| 模式 | 适用场景 | 存储位置 | 复杂度 |
|------|----------|----------|--------|
| Local（默认） | 单机、开发、小规模 | 服务端本地磁盘 | 低 |
| WebSocket | 专用存储集群 | WS 客户端节点 | 中 |
| Hybrid | 混合模式，无 WS 客户端时自动降级 | 优先 WS，降级本地 | 中 |

---

## 单机部署（默认）

### 1. 准备

```bash
# 安装 PostgreSQL
sudo apt install postgresql

# 安装 .NET 10.0 Runtime（仅运行时）
# 或使用自包含发布（无需运行时）
```

### 2. 创建数据库

```bash
sudo -u postgres psql
CREATE DATABASE fileupload;
\q

# 导入表结构
psql -h localhost -U postgres -d fileupload -f complete_schema.sql
```

### 3. 配置文件

`appsettings.json`：
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=fileupload;Username=postgres;Password=yourpassword"
  },
  "Storage": {
    "Mode": "Local",
    "LocalPath": "wwwroot/uploads"
  }
}
```

### 4. 发布 & 启动

```bash
# 发布（自包含，无需安装 .NET 运行时）
dotnet publish -c Release --sc true -o /opt/fileupload

# 清理开发配置文件
rm -f /opt/fileupload/*.runtimeconfig.dev.json

# 复制配置
cp appsettings.json /opt/fileupload/

# 启动
cd /opt/fileupload
ASPNETCORE_URLS="http://0.0.0.0:7000" \
ConnectionStrings__DefaultConnection="Host=localhost;Database=fileupload;Username=postgres;Password=xxx" \
nohup ./FileUploadServer.Web > /var/log/fileupload.log 2>&1 &
```

---

## 网关 + WS 客户端分布式部署

这种模式下，网关仅负责 API 认证/路由，文件实际存储在独立的 WS 客户端节点上。

### 步骤 1：部署网关服务器

```bash
# 同单机部署的步骤 1-3，但修改 Storage.Mode
```

`appsettings.json`（网关）：
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=db.example.com;Database=fileupload;Username=postgres;Password=xxx"
  },
  "Storage": {
    "Mode": "Hybrid",
    "LocalPath": "wwwroot/uploads",
    "Routes": [
      { "PathPattern": "/public/*", "Mode": "WebSocket" },
      { "PathPattern": "/archive/*", "Mode": "WebSocket" },
      { "PathPattern": "/private/*", "Mode": "Local" }
    ]
  },
  "PublicPath": {
    "Patterns": ["/public/*"]
  }
}
```

### 步骤 2：在网关注册 WS 客户端

```bash
# 注册存储节点
curl -X POST http://localhost:7000/api/admin/ws-clients \
  -H "Content-Type: application/json" \
  -d '{
    "description": "主存储节点 A",
    "pathPrefixes": ["/public/*", "/shared/*"],
    "storageCapacity": 1099511627776
  }'
```

响应（**仅此一次返回 secret，务必保存**）：
```json
{
  "id": "storage-node-a",
  "clientSecret": "sk-wsc-a1b2c3d4e5f6...",
  "description": "主存储节点 A"
}
```

### 步骤 3：部署 WS 客户端

在存储节点服务器上：

```bash
# 发布 WS 客户端
cd FileUploadServer.WsClient
dotnet publish -c Release --sc true -o /opt/fileupload-wsclient
rm -f /opt/fileupload-wsclient/*.runtimeconfig.dev.json
```

启动 WS 客户端：
```bash
cd /opt/fileupload-wsclient
./FileUploadServer.WsClient \
  --mode ws \
  --server ws://gateway.example.com:7000 \
  --client-id storage-node-a \
  --client-secret sk-wsc-a1b2c3d4e5f6... \
  --storage-path /data/files \
  --paths /public/*,/shared/*
```

### 步骤 4：验证

```bash
# 查看客户端连接状态
curl http://localhost:7000/api/admin/ws-clients/storage-node-a/stats

# 上传文件到 /public/ 路径（会自动路由到 WS 客户端）
curl -X POST "http://gateway:7000/api/files?key=ADMIN_KEY" \
  -F "file=@test.jpg" \
  -F "path=/public/images/test.jpg"

# 如果 WS 客户端断开，Hybrid 模式自动降级到网关本地存储
```

### 多客户端路由策略

| 策略 | 行为 | 配置 |
|------|------|------|
| PathPrefix | 按路径前缀精确匹配 | 默认，最常用 |
| RoundRobin | 同前缀下轮询 | `RouteStrategy: RoundRobin` |
| LeastStorage | 选存储用量最低的 | `RouteStrategy: LeastStorage` |
| WeightedRandom | 按容量加权随机 | `RouteStrategy: WeightedRandom` |

客户端离线 30 秒后自动 cooldown，请求转移到其他可用节点。

---

## 加密配置

### 初始化加密系统（首次）

```bash
# 交互式初始化
./FileUploadServer.Web --encrypt-init

# 或通过环境变量提供密钥
export FILE_ENCRYPTION_KEY="$(python3 -c 'import base64,os;print(base64.b64encode(os.urandom(32)).decode())')"
```

### 配置选项

```json
{
  "Encryption": {
    "KeyFilePath": "/etc/fileuploadserver/encryption.key"
  }
}
```

密钥加载优先级：
1. 环境变量 `FILE_ENCRYPTION_KEY`（base64, 32 字节）
2. 密钥文件 `Encryption:KeyFilePath`
3. 配置项 `Encryption:MasterKey`（仅开发环境）
4. 首次启动自动生成

### 恢复密钥

```bash
# 通过恢复口令重建密钥文件
./FileUploadServer.Web --recover
```

### 批量导出明文

```bash
./FileUploadServer.Web --export-plaintext /backup/decrypted
```

---

## 公共访问路径配置

```json
{
  "PublicPath": {
    "Patterns": ["/public/*", "/shared/docs/**"],
    "MaxFileSize": 52428800,
    "CacheControl": "public,max-age=604800",
    "AllowList": [],
    "DenyList": [],
    "RateLimit": {
      "PerIpPerMinute": 100,
      "PerFilePerMinute": 20,
      "ConcurrentDownloads": 50
    }
  }
}
```

设置文件公开访问：
```bash
curl -X PUT http://localhost:7000/api/admin/files/123/public \
  -H "Content-Type: application/json" \
  -d '{"isPublic": true, "publicPath": "/public/docs/report.pdf"}'
```

匿名访问：
```bash
curl http://gateway:7000/p/public/docs/report.pdf
# 无需 API Key，但受限流和 IP 白名单/黑名单约束
```

---

## 高可用部署

### Nginx 反向代理

```nginx
upstream fileupload_gateway {
    server gateway1.example.com:7000;
    server gateway2.example.com:7000;
    server gateway3.example.com:7000;
}

server {
    listen 443 ssl;
    server_name files.example.com;

    # 大文件上传
    client_max_body_size 1G;
    proxy_read_timeout 300s;

    # WebSocket 支持
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";

    location / {
        proxy_pass http://fileupload_gateway;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Real-IP $remote_addr;
    }
}
```

### 注意事项

1. **所有网关共享同一个 PostgreSQL** — `WsClients` 表、`FileLocations` 表、`ApiKeys` 表全局一致
2. **客户端 ID 全局唯一** — 每个 WS 客户端节点有唯一 ID
3. **数据库连接池** — 多网关节点注意 `MaxPoolSize` 配置
4. **定时任务去重** — `BackgroundCleanupService` 和 `KeyRotationService` 多节点会重复执行，后期可引入分布式锁

---

## 运维命令速查

```bash
# === 网关管理 ===

# 创建管理员密钥
curl -X POST "http://localhost:7000/api/admin/keys?description=admin&expireMinutes=0&keyType=Admin"

# 列出所有密钥
curl http://localhost:7000/api/admin/keys

# 清理过期密钥
curl -X DELETE http://localhost:7000/api/admin/keys/cleanup

# 查看公共文件统计
curl http://localhost:7000/api/admin/stats/public-access

# === WS 客户端管理 ===

# 注册客户端
curl -X POST http://localhost:7000/api/admin/ws-clients \
  -H "Content-Type: application/json" \
  -d '{"description":"Node A","pathPrefixes":["/public/*"],"storageCapacity":1099511627776}'

# 列出所有客户端
curl http://localhost:7000/api/admin/ws-clients

# 查看客户端状态
curl http://localhost:7000/api/admin/ws-clients/storage-node-a/stats

# 禁用客户端
curl -X PATCH http://localhost:7000/api/admin/ws-clients/storage-node-a/status \
  -H "Content-Type: application/json" \
  -d 'false'

# 注销客户端
curl -X DELETE http://localhost:7000/api/admin/ws-clients/storage-node-a

# === IP 白名单 ===

curl http://localhost:7000/api/admin/whitelist
curl -X POST "http://localhost:7000/api/admin/whitelist?ipAddress=1.2.3.4&description=office"
curl -X DELETE http://localhost:7000/api/admin/whitelist/1

# === 健康检查 ===
curl http://localhost:7000/swagger
```
