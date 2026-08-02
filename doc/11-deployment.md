# 部署运维指南
> 用途：描述 FileUploadServer 在单机/分布式/高可用三种模式下的部署流程与运维命令
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md)、[02-api-reference.md](02-api-reference.md)、[07-ws-storage.md](07-ws-storage.md)

## 目录
1. [三种部署模式](#1-三种部署模式)
2. [远程服务器信息](#2-远程服务器信息)
3. [单机部署](#3-单机部署)
4. [网关 + WS 节点分布式部署](#4-网关--ws-节点分布式部署)
5. [高可用（Nginx）部署](#5-高可用nginx部署)
6. [MCP Server 接入](#6-mcp-server-接入)
7. [部署注意事项](#7-部署注意事项)
8. [运维命令速查](#8-运维命令速查)
9. [已知部署问题](#9-已知部署问题)

## 1. 三种部署模式

| 模式 | 适用场景 | 存储位置 | 复杂度 |
|------|----------|----------|--------|
| **单机（Local）** | 开发、小规模 | 服务端本地磁盘 `wwwroot/uploads/` | 低 |
| **网关 + WS 节点（Hybrid/WebSocket）** | 专用存储集群、多机房 | WS 客户端节点磁盘 | 中 |
| **高可用（Nginx + 多网关）** | 生产级、零停机 | WS 节点（共享 PostgreSQL） | 高 |

```
单机模式                         网关 + WS 客户端模式              高可用模式
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
                           └──────┘ └──────┘ └──────┘ └──────┬───────┘   └──────┬───────┘
                                                          ┌──┼──────────────────┼──┐
                                                          ▼  ▼                  ▼  ▼
                                                      ┌──────┐ ┌──────┐   ┌──────┐ ┌──────┐
                                                      │WS Cli│ │WS Cli│   │WS Cli│ │WS Cli│
                                                      │Node A│ │Node B│   │Node C│ │Node D│
                                                      └──────┘ └──────┘   └──────┘ └──────┘
```

## 2. 远程服务器信息

| 角色 | 地址 | 访问方式 | 部署目录 |
|------|------|----------|----------|
| **网关（Web）** | `111.229.53.125:7000` | `ubuntu` + `~/.ssh/CouldServer_1.pem` | `/home/ubuntu/fileuploadserver/` |
| **WS 存储节点** | `192.168.1.4` | `laowang` + 密码 | 程序 `/home/laowang/wsclient/`，数据 `/home/laowang/wsdata/` |
| **PostgreSQL** | 网关本机 `5432` | `postgres` | 数据库 `fileupload` |
| **MCP Server** | 本地（stdio） | 配置到 Claude Code | 无需远程部署 |

## 3. 单机部署

### 3.1 前置条件

- .NET 10.0 Runtime 或使用自包含发布（`--sc true`）
- PostgreSQL 已安装

### 3.2 创建数据库

```bash
sudo -u postgres psql
CREATE DATABASE fileupload;
\q

# 导入表结构
psql -h localhost -U postgres -d fileupload -f sql/complete_schema.sql
```

### 3.3 配置连接串

`appsettings.json`：
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=fileupload;Username=postgres;Password=<your-password>"
  },
  "Storage": {
    "Mode": "Local",
    "LocalPath": "wwwroot/uploads"
  }
}
```

### 3.4 发布并启动

```bash
# 发布（自包含，无需安装 .NET 运行时）
dotnet publish -c Release --sc true -o /opt/fileupload

# 清理开发配置文件（必须，否则启动极慢）
rm -f /opt/fileupload/*.runtimeconfig.dev.json

# 复制配置
cp appsettings.json /opt/fileupload/

# 启动
cd /opt/fileupload
ASPNETCORE_URLS="http://0.0.0.0:7000" \
ConnectionStrings__DefaultConnection="Host=localhost;Database=fileupload;Username=postgres;Password=xxx" \
nohup ./FileUploadServer.Web > /var/log/fileupload.log 2>&1 &
```

### 3.5 验证

```bash
# 首页需 key（返回 401 即正常）
curl -s -o /dev/null -w '%{http_code}' http://localhost:7000/
# 预期：401

# Swagger 可访问
curl -s -o /dev/null -w '%{http_code}' http://localhost:7000/swagger
# 预期：200
```

## 4. 网关 + WS 节点分布式部署

### 4.1 网关配置

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
  }
}
```

**Storage.Mode 选项**：
- `Local` — 全部本地磁盘存储
- `WebSocket` — 全部走 WS 节点（无匹配节点时失败）
- `Hybrid` — 优先匹配 WS 节点，无匹配或节点离线时自动降级到本地

### 4.2 在网关注册 WS 客户端

```bash
curl -X POST http://localhost:7000/api/admin/ws-clients \
  -H "Content-Type: application/json" \
  -d '{
    "description": "主存储节点 A",
    "pathPrefixes": ["/public/*", "/shared/*"],
    "storageCapacity": 1099511627776
  }'
```

响应（**仅此一次返回 `clientSecret`，务必保存**）：
```json
{
  "id": "storage-node-a",
  "clientSecret": "sk-wsc-a1b2c3d4e5f6...",
  "description": "主存储节点 A"
}
```

### 4.3 部署 WS 客户端（在存储节点执行）

```bash
# 编译发布
cd FileUploadServer.WsClient
dotnet publish -c Release --sc true -o /opt/fileupload-wsclient
rm -f /opt/fileupload-wsclient/*.runtimeconfig.dev.json
```

启动：
```bash
cd /opt/fileupload-wsclient
./FileUploadServer.WsClient \
  --mode ws \
  --server ws://111.229.53.125:7000 \
  --client-id storage-node-a \
  --client-secret sk-wsc-a1b2c3d4e5f6... \
  --storage-path /home/laowang/wsdata \
  --paths "/*"
```

> 注意事项：
> - `--secret` 若遗失，通过网关 `POST /api/admin/ws-clients/{id}/regenerate-secret` 重新生成
> - `--paths "/*"` 必须加引号，防止 shell 通配符展开
> - 同一 client-id 只允许 1 个进程，多进程会争抢连接导致反复断连

### 4.4 验证 WS 连接

```bash
curl http://localhost:7000/api/admin/ws-clients/storage-node-a/stats
# 预期：isOnline = true，lastHeartbeat 持续更新
```

### 4.5 多客户端路由策略

| 策略 | 行为 | 配置 |
|------|------|------|
| PathPrefix | 按路径前缀精确匹配 | 默认，最常用 |
| RoundRobin | 同前缀下轮询 | `RouteStrategy: RoundRobin` |
| LeastStorage | 选存储用量最低的 | `RouteStrategy: LeastStorage` |
| WeightedRandom | 按容量加权随机 | `RouteStrategy: WeightedRandom` |

客户端离线 30 秒后自动 cooldown，请求转移到其他可用节点。

## 5. 高可用（Nginx）部署

### 5.1 Nginx 反向代理配置

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

### 5.2 多网关注意事项

1. **共享 PostgreSQL**：`WsClients` 表、`FileLocations` 表、`ApiKeys` 表全局一致
2. **客户端 ID 全局唯一**：每个 WS 客户端节点有唯一 ID
3. **数据库连接池**：多网关节点注意 `MaxPoolSize` 配置
4. **定时任务去重**：`BackgroundCleanupService` 和 `KeyRotationService` 多节点会重复执行，后期可引入分布式锁

## 6. MCP Server 接入

MCP Server 走 stdio，**运行在 Claude Code 所在机器（本地）**，不部署到远程。

### 6.1 配置

在项目根目录的 `.mcp.json` 中配置（或用 `claude mcp add` 命令）：

```json
{
  "mcpServers": {
    "file-upload-server": {
      "command": "dotnet",
      "args": ["run", "--project", "FileUploadServer.Mcp/FileUploadServer.Mcp.csproj"],
      "env": {
        "FILE_SERVER_BASE_URL": "http://111.229.53.125:7000",
        "FILE_SERVER_MASTER_KEY": "<Admin 密钥>"
      }
    }
  }
}
```

### 6.2 编译（可选）

```bash
dotnet publish FileUploadServer.Mcp/FileUploadServer.Mcp.csproj -c Release -o publish/mcp
```

此时 `command` 改为 `./publish/mcp/FileUploadServer.Mcp`。

## 7. 部署注意事项

1. **只上传编译产物，不上传源码**：`dotnet publish --sc true` 生成自包含二进制，无需远端安装 .NET 运行时
2. **保留远端配置**：部署流程「备份 → 上传 → 恢复」，`appsettings.json` 和 `encryption.key` 绝不能被覆盖
3. **敏感信息检查**：
   - 部署前用 `git add --dry-run` 检查提交内容
   - 扫描模式：`sk-wsc-` 长密钥、数据库密码、32 位 hex 密钥
   - `.gitignore` 已忽略 `appsettings.json`、`publish/`、`*.key`
4. **清理 `*.runtimeconfig.dev.json`**：发布目录中的此文件指向开发机路径，在 Linux 原生系统上会导致启动极慢（>30s），必须删除
5. **单项目发布**：`publish FileUploadServer.Web/FileUploadServer.Web.csproj`，**不要** `publish FileUploadServer.slnx`（会混入 Tests）
6. **WsClient 单实例**：同一 client-id 只跑 1 个进程
7. **部署前 git 提交**：确保远端代码可追溯、可回退
8. **安全网**：上传前备份 `appsettings.json` / `encryption.key`，启动验证通过后再删 `.bak`

## 8. 运维命令速查

### 8.1 服务管理

```bash
# 启动网关
cd /home/ubuntu/fileuploadserver
nohup ./FileUploadServer.Web --urls http://0.0.0.0:7000 > fileupload.log 2>&1 & echo $! > fileupload.pid

# 停止网关（精确匹配，避免 pkill -f 误杀 SSH 会话）
PID=$(pgrep -f '\./FileUploadServer\.Web' | head -1)
[ -n "$PID" ] && kill -9 $PID

# 查看网关进程
ps aux | grep '[F]ileUploadServer.Web'

# 查看端口
ss -tlnp | grep ':7000'
```

### 8.2 日志查看

```bash
# 网关日志
tail -f /home/ubuntu/fileuploadserver/fileupload.log

# WS 节点日志
tail -f /home/laowang/wsclient/wsclient.log

# 搜索错误
grep -i "error\|exception\|fail" /home/ubuntu/fileuploadserver/fileupload.log | tail -20
```

### 8.3 数据库

```bash
# PostgreSQL 状态
sudo systemctl status postgresql

# 备份数据库
pg_dump -h localhost -U postgres -d fileupload > fileupload_backup_$(date +%Y%m%d).sql

# 恢复数据库
psql -h localhost -U postgres -d fileupload < fileupload_backup_20260802.sql
```

### 8.4 密钥管理（localhost 执行）

```bash
# 创建 Admin 密钥
curl -X POST "http://localhost:7000/api/admin/keys?description=admin&expireMinutes=0&keyType=Admin"

# 列出所有密钥
curl http://localhost:7000/api/admin/keys

# 清理过期密钥
curl -X DELETE http://localhost:7000/api/admin/keys/cleanup
```

### 8.5 WS 客户端管理（localhost 执行）

```bash
# 列出所有客户端
curl http://localhost:7000/api/admin/ws-clients

# 查看客户端状态
curl http://localhost:7000/api/admin/ws-clients/<client-id>/stats

# 禁用客户端
curl -X PATCH http://localhost:7000/api/admin/ws-clients/<client-id>/status \
  -H "Content-Type: application/json" -d '{"isEnabled": false}'

# 重新生成密钥
curl -X POST http://localhost:7000/api/admin/ws-clients/<client-id>/regenerate-secret

# 注销客户端
curl -X DELETE http://localhost:7000/api/admin/ws-clients/<client-id>
```

### 8.6 Systemd 服务（可选，用于自动重启）

```ini
# /etc/systemd/system/fileupload.service
[Unit]
Description=FileUploadServer Web Gateway
After=network.target postgresql.service

[Service]
Type=simple
WorkingDirectory=/home/ubuntu/fileuploadserver
ExecStart=/home/ubuntu/fileuploadserver/FileUploadServer.Web --urls http://0.0.0.0:7000
Restart=always
RestartSec=10
User=ubuntu

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable fileupload
sudo systemctl start fileupload
sudo systemctl status fileupload
```

### 8.7 健康检查

```bash
# 网关首页（预期 401，说明 API Key 中间件正常工作）
curl -s -o /dev/null -w '%{http_code}' http://111.229.53.125:7000/

# Swagger（预期 200）
curl -s -o /dev/null -w '%{http_code}' http://111.229.53.125:7000/swagger

# API 列表（需有效 key）
curl -s "http://111.229.53.125:7000/api/files?key=<ADMIN_KEY>" | head -c 200
```

## 9. 已知部署问题

### 9.1 公开文件访问（/p/）已屏蔽

- **时间**：2026-08-02
- **状态**：`PublicFileMiddleware` 未注册，`/p/` 路径返回 401/404
- **根因**：WS 存储加密文件经 `/p/` 访问时服务端解密失败（AesGcmDecryptStream tag mismatch）；中间件直接连 WS 节点违背「所有文件访问统一走 API」的分层架构
- **整改方向**：公开访问统一走 `FileApiController.Download` 封装，或公开文件限定本地磁盘存储

### 9.2 `ApiKeyAuthMiddleware` 的 `/p/` 放行 bug

- `StartsWithSegments("/p/")` 实测对 `/p/public/...` 返回 `False`
- 该 bug 此前被 `PublicFileMiddleware` 屏蔽前掩盖
- 修复方案：改为 `StartsWithSegments("/p")`

### 9.3 `pkill -f` 误杀 SSH 会话

- `pkill -f 'FileUploadServer.Web'` 匹配到 ssh 命令行自身导致连接断开（exit 255）
- 解决：用 `pgrep -f '\./FileUploadServer\.Web'` 精确取 PID 再 kill

### 9.4 `text file busy` 错误

- 运行中的可执行文件无法被 scp 覆盖
- 解决：先停进程再上传

### 9.5 nohup 挂起 SSH

- nohup 启动的子进程持有 ssh 管道，导致命令超时挂起
- 解决：启动命令与验证命令分离执行

### 9.6 多进程争抢连接

- 同一 client-id 的多个 WsClient 进程反复断连（`Disconnected: Connection lost`）
- 解决：确保同一 client-id 只跑 1 个进程

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览
- [02-api-reference.md](02-api-reference.md) — 全部 HTTP API 端点
- [07-ws-storage.md](07-ws-storage.md) — WS 分布式存储细案
- [12-bug-tracker.md](12-bug-tracker.md) — 踩坑记录（含部署踩坑）
