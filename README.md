# FileUploadServer

一个简单、带临时密钥鉴权的文件上传分享服务器，基于 ASP.NET Core 10.0 开发。

## 功能特点

- 🔐 **临时密钥鉴权**：所有访问需要携带有效密钥，密钥支持自动过期、手动删除
- 🌐 **局域网可访问**：默认监听 `0.0.0.0:5005`，支持局域网内设备访问
- 🕐 **自动过期**：密钥默认1小时过期，即使忘记删除也会自动失效
- 📝 **网页管理**：自带简洁网页，可以直接在网页上传/删除文件
- 🚀 **简单部署**：依赖PostgreSQL，用EF Core自动建表，一键运行

## 技术栈

- 后端：ASP.NET Core 10.0
- 数据库：PostgreSQL
- 鉴权：自定义API Key中间件
- ORM：Entity Framework Core

## 本地部署步骤

### 1. 环境准备

- 安装 .NET 10.0 SDK
- 安装 PostgreSQL 12+

### 2. 初始化数据库

```bash
# 进入PostgreSQL命令行
sudo -u postgres psql

# 创建数据库和用户
CREATE DATABASE fileupload;
CREATE USER postgres WITH PASSWORD 'your-password';
GRANT ALL PRIVILEGES ON DATABASE fileupload TO postgres;
\q
```

### 3. 修改连接字符串

编辑 `FileUploadServer.Web/appsettings.json`，修改连接字符串中的密码：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=fileupload;Username=postgres;Password=your-password"
  }
}
```

### 4. 执行数据库迁移

```bash
# 在项目根目录执行
dotnet ef database update
```

这一步会自动创建所需的表结构：
- `ApiKeys`：存储访问密钥
- `Files`：存储文件信息

### 5. 运行项目

```bash
cd FileUploadServer.Web
dotnet run
```

启动后访问：`http://localhost:5005`

## 使用说明

### 管理员操作（仅localhost可调用）

只有本机可以创建/删除密钥，外部访问必须携带有效密钥。

#### 创建新密钥

```bash
curl -X POST "http://localhost:5005/api/admin/keys?description=my-upload&expireMinutes=60"
```

参数：
- `description`：密钥备注说明
- `expireMinutes`：过期时间（分钟，默认60）

返回示例：
```json
{
  "id": 1,
  "key": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "description": "my-upload",
  "createdAt": "2026-03-30T00:00:00Z",
  "expiresAt": "2026-03-30T01:00:00Z",
  "isDeleted": false
}
```

#### 列出所有密钥

```bash
curl http://localhost:5005/api/admin/keys
```

#### 删除密钥

```bash
curl -X DELETE "http://localhost:5005/api/admin/keys/{key}"
```

#### 清理过期密钥

```bash
curl -X DELETE http://localhost:5005/api/admin/keys/cleanup
```

### 用户访问

访问首页（带密钥）：
```
http://your-server-ip:5005?key={your-key}
```

上传文件：
表单需要包含 `key` 字段，可以直接在网页上传

下载文件：
```
http://your-server-ip:5005/api/files/download/{file-id}?key={your-key}
```

## 工作流示例

1. 需要分享文件时：
   - 本机调用创建接口生成临时密钥
   - 将带密钥的下载链接发给对方
2. 使用完成后：
   - 调用删除接口立即失效链接
   - 忘记删除也会在设定时间后自动过期

## 许可

MIT
