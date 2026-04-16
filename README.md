# FileUploadServer

一个简单、带分级权限系统的文件上传分享服务器，基于 ASP.NET Core 10.0 开发。

## 功能特点

- 🔐 **分级权限系统**
  - **Admin Key（管理密钥）：** 可管理所有文件，仅内网可申请
  - **Temporary Key（临时密钥）：** 仅能访问自有文件，公网可申请，过期后文件自动删除
- 🛡️ **IP白名单保护**
  - 公网申请临时密钥需要IP在白名单中
  - 白名单管理仅内网可操作
  - 支持添加/删除/查看白名单IP
- 🌐 **公网部署**：监听 `0.0.0.0:7000`，支持公网访问
- 🕐 **自动过期清理**：后台服务每小时自动清理过期临时密钥及其关联文件
- 📝 **网页管理**：自带简洁网页，权限控制覆盖API和网页端
- 🚀 **简单部署**：依赖PostgreSQL，支持SQL脚本升级

## 技术栈

- 后端：ASP.NET Core 10.0
- 数据库：PostgreSQL（端口5432）
- 鉴权：自定义API Key中间件
- ORM：Entity Framework Core
- 后台服务：IHostedService 自动清理

## 权限说明

### 密钥类型

| 类型 | 申请方式 | 权限 | 过期后 |
|------|----------|------|--------|
| Admin | 内网（localhost） | 管理所有文件 | - |
| Temporary | 公网API | 仅访问自有文件 | 文件自动删除 |

### 权限规则

| 操作 | Admin Key | Temporary Key |
|------|-----------|---------------|
| GET /api/files | 返回所有文件 | 仅返回自有文件 |
| GET /api/files/{id} | 可访问所有 | 仅可访问自有 |
| POST /api/files | 可上传 | 可上传（自动关联） |
| DELETE /api/files/{id} | 可删除所有 | 仅可删除自有 |
| 网页访问 | 可管理所有 | 仅可管理自有 |

## 远程部署（生产环境）

### 服务器信息

- **地址：** 111.229.53.125
- **端口：** 7000
- **数据库：** PostgreSQL（端口5432）

### 数据库升级

使用SQL脚本升级（不使用EF Core迁移）：

```bash
# 在远程服务器执行
psql -h localhost -p 5432 -U postgres -d fileupload -f upgrade_v2.sql
```

### 启动服务

```bash
cd /home/ubuntu/fileuploadserver
nohup dotnet FileUploadServer.Web.dll --urls "http://0.0.0.0:7000" > fileupload.log 2>&1 &
```

## 使用说明

### 公网申请临时密钥（需要IP在白名单中）

```bash
curl -X POST "http://111.229.53.125:7000/api/public/keys?description=temp-share&expireMinutes=60"
```

### IP白名单管理（仅localhost可调用）

#### 查看白名单
```bash
ssh -i ~/.ssh/CouldServer_1.pem ubuntu@111.229.53.125 "curl http://localhost:7000/api/admin/whitelist"
```

#### 添加IP到白名单
```bash
ssh -i ~/.ssh/CouldServer_1.pem ubuntu@111.229.53.125 "curl -X POST 'http://localhost:7000/api/admin/whitelist?ipAddress=你的IP&description=描述'"
```

#### 从白名单移除IP
```bash
ssh -i ~/.ssh/CouldServer_1.pem ubuntu@111.229.53.125 "curl -X DELETE http://localhost:7000/api/admin/whitelist/{ID}"
```

参数：
- `description`：密钥备注说明
- `expireMinutes`：过期时间（分钟，最大1440分钟=24小时）

### 管理员操作（仅localhost可调用）

```bash
# SSH到远程服务器后执行
ssh -i ~/.ssh/CouldServer_1.pem ubuntu@111.229.53.125

# 创建管理密钥
curl -X POST "http://localhost:7000/api/admin/keys?description=admin-key&expireMinutes=1440&keyType=Admin"

# 列出所有密钥
curl http://localhost:7000/api/admin/keys

# 清理过期密钥
curl -X DELETE http://localhost:7000/api/admin/keys/cleanup
```

### 用户访问

访问首页（带密钥）：
```
http://111.229.53.125:7000?key={your-key}
```

## 本地开发

### 环境准备

- 安装 .NET 10.0 SDK
- 安装 PostgreSQL 12+

### 运行项目

```bash
cd FileUploadServer.Web
dotnet run
```

## 许可

MIT
