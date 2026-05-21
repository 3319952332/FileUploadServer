# FileUploadServer 下一步开发计划

**创建时间**：2025-06-29  
**版本**：v1.0  
**状态**：规划中

---

## 📋 目录

1. [概述](#概述)
2. [功能一：WebSocket 客户端架构](#功能一websocket-客户端架构)
3. [功能二：公共访问路径](#功能二公共访问路径)
4. [功能三：文件存储加密](#功能三文件存储加密)
5. [实施时间线](#实施时间线)
6. [迁移策略](#迁移策略)

---

## 概述

本计划描述 FileUploadServer 的两个核心功能演进：

1. **架构重构**：引入 WebSocket 持久连接，将文件存储职责下沉到客户端，服务端仅作为转发网关
2. **功能增强**：新增公共访问路径，支持无需 API Key 的匿名文件访问

---

## 功能一：WebSocket 客户端架构

### 🎯 目标

将当前的"服务器存储"模式演进为"网关转发 + 客户端存储"模式：
- 服务端：仅负责认证、路由、转发、流量控制
- 客户端：负责实际的文件存储、读取、删除操作

### 🏗️ 架构设计

```
┌─────────┐   HTTP   ┌──────────────────┐   WebSocket   ┌──────────────┐
│  用户   │ ◀──────▶ │  FileUploadServer │ ◀──────────▶ │  WS 客户端   │
└─────────┘          │   (网关/转发)     │               │  (文件存储)   │
                     └──────────────────┘               └──────────────┘
                          ▲  ▲
                          │  │
                     ┌────┘  └────┐
                     │             │
               ┌──────────┐  ┌──────────┐
               │ WS客户端1 │  │ WS客户端2 │  ... 多客户端集群
               └──────────┘  └──────────┘
```

### 📐 设计要点

#### 1. WebSocket 连接管理

**协议路径**：
```
/ws/connect?clientId={clientId}&token={token}
```

**认证机制**：
- 新增 `Client` 实体和客户端密钥管理
- 每个客户端有独立的 ID 和 Secret
- 连接时验证客户端身份
- 服务端维护连接池：`Dictionary<ClientId, WebSocketConnection>`

**连接状态**：
```csharp
public class WsClientConnection
{
    public string ClientId { get; set; }
    public WebSocket WebSocket { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public long TotalStorageBytes { get; set; } // 客户端上报的存储容量
    public List<string> SupportedPaths { get; set; } // 支持的路径前缀
}
```

#### 2. 心跳机制

- 客户端每 30 秒发送 Ping
- 服务端 60 秒未收到心跳则标记断开
- 断开后自动重连逻辑（客户端实现）

#### 3. 协议设计（二进制 + JSON 混合）

**控制消息（JSON）**：
```json
{
  "type": "upload_request | download_request | delete_request | list_request",
  "requestId": "uuid",
  "path": "/path/to/file",
  "fileName": "example.txt",
  "fileSize": 1024000,
  "metadata": { ... }
}
```

**数据传输（二进制帧）**：
- 文件内容使用二进制帧传输
- 支持分块传输（chunked）
- 每块大小建议 64KB - 1MB

#### 4. 服务端转发逻辑

**上传流程**：
```
1. 用户 POST /api/upload?key=xxx&path=/public/abc.jpg
   └─> 服务端验证 API Key 权限
   
2. 服务端选择合适的 WS 客户端（根据 path 路由）
   └─> 发送 UPLOAD_REQUEST 消息到客户端
   
3. 客户端返回 ACK，准备接收数据
   └─> 服务端将用户上传的数据流转发给客户端
   
4. 客户端存储完成后发送 UPLOAD_COMPLETE
   └─> 服务端记录文件元数据（路径、大小、哈希、所在客户端）
   
5. 服务端返回上传结果给用户
```

**下载流程**：
```
1. 用户 GET /download/{fileId}?key=xxx
   └─> 服务端验证权限，查询文件所在的客户端
   
2. 服务端发送 DOWNLOAD_REQUEST 到对应 WS 客户端
   
3. 客户端返回文件数据流（分块）
   └─> 服务端流式转发给用户
   
4. 传输完成，客户端发送 DOWNLOAD_COMPLETE
```

#### 5. 文件元数据存储

**新增表结构**：
```sql
CREATE TABLE file_locations
(
    id UUID PRIMARY KEY,
    file_path VARCHAR(1024) NOT NULL,
    file_name VARCHAR(255) NOT NULL,
    file_size BIGINT NOT NULL,
    file_hash VARCHAR(64), -- SHA256
    client_id VARCHAR(64) NOT NULL, -- 所在的 WS 客户端 ID
    api_key_id UUID NOT NULL, -- 所属的 API Key
    is_public BOOLEAN DEFAULT FALSE, -- 是否在公共路径
    created_at TIMESTAMP NOT NULL,
    expires_at TIMESTAMP,
    UNIQUE (file_path, client_id)
);

CREATE INDEX idx_file_locations_path ON file_locations(file_path);
CREATE INDEX idx_file_locations_client ON file_locations(client_id);
```

#### 6. 客户端 SDK

提供标准客户端实现（C#）：
```csharp
public interface IFileStorageClient
{
    Task ConnectAsync(string serverUrl, string clientId, string clientSecret);
    Task DisconnectAsync();
    
    // 存储实现（由具体客户端提供）
    Task<byte[]> ReadFileAsync(string path);
    Task WriteFileAsync(string path, byte[] data);
    Task DeleteFileAsync(string path);
    Task<bool> FileExistsAsync(string path);
    Task<long> GetFileSizeAsync(string path);
}
```

#### 7. 多客户端路由策略

- **路径前缀路由**：不同客户端负责不同的路径前缀
- **负载均衡**：同一前缀下多个客户端时，轮询或按存储容量分配
- **故障转移**：客户端断开时，自动将路由切换到其他可用客户端

---

## 功能二：公共访问路径

### 🎯 目标

支持配置特定路径前缀，该路径下的文件无需 API Key 即可匿名访问。

### 📐 设计要点

#### 1. 配置方式

**appsettings.json**：
```json
{
  "PublicPaths": [
    "/public/*",
    "/shared/*",
    "/assets/*.jpg",
    "/static/**/*.png"
  ],
  "PublicPathSettings": {
    "MaxFileSize": 52428800, // 50MB
    "RateLimitPerMinute": 100,
    "AllowList": ["192.168.1.0/24"], // 可选 IP 白名单
    "DenyList": []
  }
}
```

#### 2. 访问路径

**匿名访问 URL**：
```
GET /p/{filePath}
例如：
GET /p/public/documents/readme.pdf
GET /p/assets/logo.png
```

**区别于认证访问**：
```
GET /api/files/{fileId}?key=xxx  (需要认证)
GET /p/{filePath}                 (无需认证)
```

#### 3. 权限判断逻辑

```csharp
public bool IsPublicPath(string path)
{
    // 1. 检查路径是否匹配公共路径规则
    if (!MatchesPublicPathPattern(path))
        return false;
    
    // 2. 检查文件元数据中的 is_public 标记
    var fileMeta = _fileLocationRepository.GetByPath(path);
    if (fileMeta == null || !fileMeta.IsPublic)
        return false;
    
    // 3. 检查 IP 限制（如果配置了）
    if (!IsIpAllowed(HttpContext.Connection.RemoteIpAddress))
        return false;
    
    return true;
}
```

#### 4. 限流保护

公共接口必须有限流：
- 基于 IP 的速率限制（如 100 次/分钟）
- 基于文件的访问频率限制
- 总带宽限制（防止 DDoS）

使用 ASP.NET Core RateLimiting：
```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("public-file-policy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));
});
```

#### 5. 缓存策略

公共文件建议启用缓存：
```csharp
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // 缓存 7 天
        ctx.Context.Response.Headers["Cache-Control"] = "public,max-age=604800";
    }
});
```

#### 6. 管理 API

```
# 设置文件为公共访问
PUT /api/admin/files/{fileId}/public
{
    "isPublic": true
}

# 查询所有公共文件
GET /api/admin/files/public?page=1&size=20

# 查看公共文件访问统计
GET /api/admin/stats/public-access
```

---

## 实施时间线

### Phase 1: 公共访问路径（1-2 周）
- [ ] 设计数据库表结构变更
- [ ] 实现公共路径匹配逻辑
- [ ] 实现 /p/ 匿名访问端点
- [ ] 添加限流保护
- [ ] 实现管理 API（设置/取消公共访问）
- [ ] 单元测试 + 集成测试
- [ ] 文档更新

### Phase 1.5: 文件存储加密（2-3 周）
- [ ] 实现 AES-256-GCM 分块加密/解密流
- [ ] 实现密钥管理（加载、生成、轮换）
- [ ] 实现多密钥槽机制（Key Slot），支持恢复口令
- [ ] 实现恢复命令行工具（--recover / --encrypt-init / --encrypt-add-slot）
- [ ] 实现明文导出命令（--export-plaintext），应急迁移用
- [ ] 修改文件存储逻辑，写入时加密、读取时透明解密
- [ ] 确保下载/预览返回解密后的原始数据，用户完全无感
- [ ] 文件名随机化，原始文件名仅存数据库
- [ ] 数据库表结构变更（加密版本、密钥版本、磁盘文件名、文件哈希）
- [ ] 密钥轮换后台任务
- [ ] 旧文件迁移（明文 → 加密）
- [ ] 性能测试（对比加密前后的吞吐量）
- [ ] 安全审计（确认磁盘上无明文残留）
- [ ] 文档更新（密钥备份、恢复口令、轮换、迁移指南）

### Phase 2: WebSocket 基础框架（2-3 周）
- [ ] WebSocket 连接管理中间件
- [ ] 客户端认证机制
- [ ] 心跳和连接状态维护
- [ ] 基础消息协议定义
- [ ] 客户端 SDK 基础实现

### Phase 3: 文件转发逻辑（2-3 周）
- [ ] 上传转发实现
- [ ] 下载转发实现
- [ ] 删除转发实现
- [ ] 文件元数据存储
- [ ] 错误处理和重试机制

### Phase 4: 高级功能（1-2 周）
- [ ] 多客户端路由
- [ ] 负载均衡
- [ ] 故障转移
- [ ] 性能优化
- [ ] 完整的集成测试

**总预计时间**：8-13 周

---

## 迁移策略

### 向后兼容

1. **保持原有 API 不变**：现有 `/api/upload`, `/api/files` 等接口继续工作
2. **配置开关**：可配置是使用"本地存储"还是"WS 客户端存储"
3. **混合模式**：部分路径使用本地存储，部分路径使用 WS 客户端

### 数据迁移

现有文件迁移到 WS 客户端模式：
1. 编写迁移脚本，扫描现有文件
2. 逐个上传到 WS 客户端
3. 更新元数据记录
4. 验证完成后删除本地文件

### 回滚方案

如果 WS 模式遇到问题，可随时切回本地存储模式：
- 配置文件中切换 `StorageMode: "Local" | "WebSocket"`
- 所有 API 接口行为保持一致

---

## 风险与注意事项

1. **WebSocket 连接稳定性**：需要完善的重连和故障转移机制
2. **内存占用**：大量并发文件传输时注意内存控制，流式处理
3. **客户端兼容性**：提供多语言客户端 SDK（至少 C# / Python）
4. **安全风险**：公共路径匿名访问，注意防止路径遍历、内容扫描
5. **性能**：转发模式下，服务端带宽会成为瓶颈，需要流量控制
6. **密钥安全**：多密钥槽机制已大幅降低密钥丢失风险，但仍需妥善保管恢复口令
7. **密钥泄露**：密钥文件泄露时需及时轮换，恢复口令未泄露则文件仍安全

---

## 功能三：文件存储加密

### 🎯 目标

文件落盘时进行加密存储，实现两个核心安全目标：

1. **防数据泄露**：即使攻击者直接访问服务器硬盘，也无法读取文件内容
2. **防恶意执行**：加密后文件不再是原始格式，即使上传了木马/脚本，也无法被直接运行或触发

### 🏗️ 设计思路

```
上传流程：
用户文件 → 服务端接收 → 加密（AES-256 + 随机IV） → 加密文件写入磁盘 → 原始文件名/元数据存数据库

下载流程：
请求文件 → 从磁盘读取加密文件 → 解密（AES-256 + 对应IV） → 返回原始文件给用户
```

### 📐 设计要点

#### 1. 加密算法选择

**推荐方案：AES-256-GCM**

| 对比项 | AES-256-GCM | AES-256-CBC | ChaCha20-Poly1305 |
|--------|-------------|-------------|-------------------|
| 认证加密 | ✅ 内置 | ❌ 需额外 HMAC | ✅ 内置 |
| 性能（有AES-NI） | ⚡ 最快 | ⚡ 快 | 🟡 中等 |
| 性能（无AES-NI） | 🟡 中等 | 🟡 中等 | ⚡ 快 |
| .NET 支持 | ✅ 原生 | ✅ 原生 | ✅ .NET 8+ |

选择 AES-256-GCM 的理由：
- 提供认证加密（AEAD），同时保证机密性和完整性
- 服务器通常有 AES-NI 硬件加速，性能最优
- .NET 原生支持，无需第三方库

#### 2. 密钥管理

**分层密钥架构**：

```
Master Key（主密钥）
  └─ 每个文件派生 File Key（文件密钥）
       └─ 每次加密使用随机 IV/Nonce
```

**主密钥存储方案**：

| 方案 | 安全性 | 复杂度 | 推荐场景 |
|------|--------|--------|----------|
| 配置文件（appsettings.json） | ⭐⭐ | ⭐ | 快速上线 |
| 环境变量 | ⭐⭐⭐ | ⭐⭐ | 容器化部署 |
| 独立密钥文件（权限600） | ⭐⭐⭐⭐ | ⭐⭐ | 生产环境推荐 |
| HSM / 云 KMS | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | 高安全要求 |

**推荐方案**：独立密钥文件 + 环境变量回退

```csharp
// 密钥加载优先级
// 1. 环境变量 FILE_ENCRYPTION_KEY（base64编码的32字节密钥）
// 2. 密钥文件 /etc/fileuploadserver/encryption.key（权限600）
// 3. appsettings.json 中的 Encryption:MasterKey
// 4. 首次启动自动生成并保存到密钥文件
```

**密钥轮换**：
- 支持密钥轮换，新旧密钥并存期
- 每个加密文件记录使用的密钥版本号
- 解密时根据版本号选择对应密钥
- 后台任务逐步用新密钥重新加密旧文件

#### 3. 加密文件存储格式

**文件头格式（二进制，固定 48 字节）**：

```
┌──────────┬──────────┬──────────┬──────────────┐
│ Magic    │ Version  │ KeyVer   │ Nonce (12B)  │
│ 4 bytes  │ 2 bytes  │ 2 bytes  │ 12 bytes     │
└──────────┴──────────┴──────────┴──────────────┘
┌────────────────────────────────────────────────┐
│ Ciphertext + Auth Tag (16 bytes appended)      │
│ 变长                                           │
└────────────────────────────────────────────────┘
```

| 字段 | 大小 | 说明 |
|------|------|------|
| Magic | 4B | 固定标识 `0x46554543`（"FUEC" = File Upload Encrypted） |
| Version | 2B | 加密格式版本，当前 `0x0001` |
| KeyVer | 2B | 密钥版本号，用于密钥轮换 |
| Nonce | 12B | GCM 随机 Nonce，每次加密唯一 |
| Ciphertext | 变长 | 加密后的文件内容 |
| Auth Tag | 16B | GCM 认证标签，附加在密文末尾 |

**为什么每个文件独立 Nonce？**
- GCM 模式下，同一密钥 + 同一 Nonce = 严重安全漏洞
- 每个文件使用随机 12 字节 Nonce，保证唯一性
- 2^96 种可能，碰撞概率可忽略

#### 4. 文件名加密

原始文件名不能明文存储在磁盘上，防止信息泄露：

**方案 A：随机文件名（推荐）**
```
磁盘文件名 = SHA256(文件ID + MasterKey截断)[0:32].hex
示例：a3f5b2c8d1e4f6a7b8c9d0e1f2a3b4c5
保留原始扩展名？→ 不保留！扩展名也是信息泄露源
```

**方案 B：加密文件名**
```
磁盘文件名 = Base64(AES-GCM(原始文件名))
```

推荐方案 A，简单且安全。原始文件名仅存储在数据库中。

#### 5. 目录结构

```
uploads/
├── a3/
│   ├── a3f5b2c8d1e4f6a7b8c9d0e1f2a3b4c5
│   └── a3e7d9f1b2c4a6e8d0f2a4b6c8e0d1f3
├── b7/
│   └── b7a1c3e5d7f9a1b3c5d7e9f1a3b5c7d9
└── ...
```

- 使用文件名前 2 字符作为子目录，避免单目录文件过多
- 文件名无扩展名，无法通过扩展名判断文件类型

#### 6. 数据库变更

```sql
-- 新增加密相关字段
ALTER TABLE files ADD COLUMN encryption_version SMALLINT DEFAULT 1;
ALTER TABLE files ADD COLUMN key_version SMALLINT DEFAULT 1;
ALTER TABLE files ADD COLUMN disk_file_name VARCHAR(64) NOT NULL;
ALTER TABLE files ADD COLUMN file_hash VARCHAR(64); -- 原始文件的 SHA256

-- 索引
CREATE INDEX idx_files_disk_name ON files(disk_file_name);
```

#### 7. 性能影响评估

| 操作 | 无加密 | AES-256-GCM | 性能损失 |
|------|--------|-------------|----------|
| 上传 100MB | ~1.0s | ~1.1s | ~10% |
| 下载 100MB | ~1.0s | ~1.1s | ~10% |
| 上传 1GB | ~10s | ~11s | ~10% |

AES-NI 硬件加速下，AES-GCM 的性能开销约 5-15%，对用户体验影响极小。

#### 8. 流式加密

大文件不能一次性加载到内存，必须流式处理：

```csharp
public class AesGcmStream : Stream
{
    // 加密：读入明文 → 分块加密 → 写出密文
    // 解密：读入密文 → 分块解密 → 写出明文
    // 块大小：64KB（平衡内存和性能）
    
    // 上传流程：
    // Request Body → AesGcmEncryptStream → FileStream
    
    // 下载流程：
    // FileStream → AesGcmDecryptStream → Response Body
}
```

**注意**：GCM 模式本身不支持真正的流式加密（需要完整数据才能计算 Auth Tag），需要调整方案：

**方案 A：分块 GCM（推荐）**
- 将文件分成固定大小的块（如 1MB）
- 每块独立用 GCM 加密，有独立的 Nonce 和 Auth Tag
- 支持真正的流式处理和随机读取

```
文件格式：
[Header 48B] [Chunk1: Nonce12B + Ciphertext + Tag16B] [Chunk2: ...] ...
每块结构：
  Nonce (12B) | Ciphertext (1MB) | Auth Tag (16B)
```

**方案 B：使用 AES-256-CTR + HMAC**
- CTR 模式支持流式加密
- 但需要额外 HMAC 保证完整性

推荐方案 A，安全性和性能兼顾。

#### 9. 透明解密保证（用户无感）

加密对用户完全透明，预览和下载时返回的是解密后的原始数据：

**下载/预览流程**：
```
用户请求 → 验证权限 → 从磁盘读取加密文件 → 自动解密 → 返回原始文件
                                                     ↑
                                          用户完全无感，和未加密时体验一致
```

**关键保证**：
- ✅ 下载的文件 = 原始上传的文件，字节级一致
- ✅ 预览图片/PDF/视频 = 解密后直接返回，浏览器正常渲染
- ✅ Content-Type / Content-Disposition 等响应头与原始文件匹配
- ✅ 文件名、大小、类型等元数据从数据库读取，不受加密影响
- ✅ 所有 API 接口行为不变，前端/调用方无需任何改动

**实现要点**：
```csharp
// 下载时自动解密，调用方无感
public async Task<IActionResult> DownloadFile(Guid fileId)
{
    var file = await _fileService.GetByIdAsync(fileId);
    var encryptedPath = GetDiskPath(file.DiskFileName);
    
    // 流式解密：加密文件 → 解密流 → 直接写入 Response Body
    var decryptStream = new AesGcmDecryptStream(File.OpenRead(encryptedPath), key, ...);
    
    return File(decryptStream, file.ContentType, file.OriginalFileName);
}
```

#### 10. 密钥恢复机制（防永久丢失）

⚠️ 密钥丢失 ≠ 数据永久丢失！采用 **多密钥槽（Key Slot）** 机制，参考 LUKS 磁盘加密设计：

**核心思路**：Master Key 是随机生成的，但用多个恢复口令分别加密 Master Key，任一口令都能解出 Master Key。

```
┌─────────────────────────────────────────────────────┐
│                   Master Key (32B)                   │
│              随机生成，实际用于文件加密                │
└──────────┬──────────┬──────────┬────────────────────┘
           │          │          │
     Slot 0 加密  Slot 1 加密  Slot 2 加密
     (自动口令)    (恢复口令1)   (恢复口令2)
           │          │          │
           ▼          ▼          ▼
     encryption.key  用户记住    离线备份
     (服务器使用)    的口令      (纸质/USB)
```

**密钥槽设计**：

| 槽位 | 用途 | 存储位置 | 说明 |
|------|------|----------|------|
| Slot 0 | 服务运行 | `/etc/fileuploadserver/encryption.key` | 服务器自动使用，无需人工干预 |
| Slot 1 | 恢复口令 | 管理员记忆 | 人工设定的强口令，紧急恢复用 |
| Slot 2 | 离线备份 | 纸质/USB/保险柜 | 最长恢复口令，打印封存 |
| Slot 3-7 | 预留 | - | 可随时添加，如给其他管理员 |

**密钥文件格式（encryption.key）**：

```json
{
  "version": 1,
  "created": "2025-07-01T00:00:00Z",
  "slots": [
    {
      "slotIndex": 0,
      "type": "auto",
      "created": "2025-07-01T00:00:00Z",
      "encryptedMasterKey": "base64...",  // 用自动口令加密的 Master Key
      "salt": "base64...",
      "iterations": 600000,               // PBKDF2 迭代次数
      "iv": "base64...",
      "tag": "base64..."
    },
    {
      "slotIndex": 1,
      "type": "passphrase",
      "created": "2025-07-01T00:00:00Z",
      "hint": "我的常用口令",
      "encryptedMasterKey": "base64...",
      "salt": "base64...",
      "iterations": 600000,
      "iv": "base64...",
      "tag": "base64..."
    }
  ]
}
```

**恢复口令加密 Master Key 的流程**：
```
1. 用户输入恢复口令
2. PBKDF2(口令, salt, 600000次) → 派生密钥
3. 用派生密钥通过 AES-256-GCM 加密 Master Key
4. 存储加密结果到对应 Slot
```

**恢复流程**（Master Key 文件丢失或损坏时）：
```
1. 运行恢复命令：dotnet FileUploadServer.Web.dll --recover
2. 输入任一恢复口令
3. 系统从对应 Slot 解密出 Master Key
4. 重新生成 encryption.key 文件
5. 服务恢复正常
```

**管理命令**：
```bash
# 初始化加密（首次启用时）
dotnet FileUploadServer.Web.dll --encrypt-init
# → 提示设置恢复口令（至少1个）

# 添加恢复口令
dotnet FileUploadServer.Web.dll --encrypt-add-slot
# → 输入新口令 + 确认

# 移除恢复口令
dotnet FileUploadServer.Web.dll --encrypt-remove-slot --slot 2

# 紧急恢复
dotnet FileUploadServer.Web.dll --recover
# → 输入恢复口令 → 重建密钥文件

# 导出所有文件为明文（应急迁移）
dotnet FileUploadServer.Web.dll --export-plaintext --output /backup/plaintext/
# → 批量解密所有文件到指定目录
```

**安全与可用性平衡**：

| 场景 | 结果 |
|------|------|
| 服务器密钥文件丢失 | ✅ 用恢复口令恢复 |
| 忘记恢复口令 | ✅ 用离线备份口令恢复 |
| 服务器全盘损坏 | ✅ 恢复口令 + 数据库备份 + 加密文件 = 完整恢复 |
| 所有口令都丢失 | ❌ 确实无法恢复（但3个槽位都丢的概率极低） |
| 密钥文件泄露 | ⚠️ 需要密钥轮换，但恢复口令未泄露则文件仍安全 |

**最佳实践建议**：
- 至少设置 2 个恢复口令（一个常用 + 一个离线备份）
- 离线备份口令打印在纸上，存放在安全位置
- 定期（每季度）测试恢复流程，确保口令有效

#### 11. 防恶意执行分析

加密存储如何防止木马/恶意文件被执行：

| 攻击场景 | 无加密 | 有加密 | 防护效果 |
|----------|--------|--------|----------|
| 直接访问磁盘执行 .exe | ✅ 可执行 | ❌ 加密后非有效PE | ✅ 完全阻止 |
| Web 路径直接访问 .php/.jsp | ✅ 可触发 | ❌ 加密后非有效脚本 | ✅ 完全阻止 |
| 上传 .sh 然后 ssh 执行 | ✅ 可执行 | ❌ 加密后非有效脚本 | ✅ 完全阻止 |
| 通过下载接口下载后本地执行 | ✅ 可执行 | ✅ 下载时解密，恢复原始文件 | ⚠️ 无法阻止 |

**结论**：加密存储能有效防止**服务器端**的恶意文件执行，但无法阻止用户下载后在本地执行。需配合以下措施：

- 可选：文件上传时进行病毒扫描（ClamAV 集成）
- 可选：限制可上传的文件类型（白名单）
- 下载时添加 `Content-Disposition: attachment` 头，防止浏览器直接执行

---

### 实施计划

**Phase 1.5: 文件存储加密（2-3 周）**

- [ ] 实现 AES-256-GCM 分块加密/解密流
- [ ] 实现密钥管理（加载、生成、轮换）
- [ ] 修改文件存储逻辑，写入时加密、读取时解密
- [ ] 文件名随机化，原始文件名仅存数据库
- [ ] 数据库表结构变更（加密版本、密钥版本、磁盘文件名、文件哈希）
- [ ] 密钥轮换后台任务
- [ ] 旧文件迁移（明文 → 加密）
- [ ] 性能测试（对比加密前后的吞吐量）
- [ ] 安全审计（确认磁盘上无明文残留）
- [ ] 文档更新（密钥备份、轮换、迁移指南）

---

## 验收标准

- [ ] 公共路径文件可在不提供 API Key 的情况下正常下载
- [ ] 公共接口有完善的限流保护
- [ ] WebSocket 客户端能稳定连接并维持心跳
- [ ] 文件上传能通过 WS 客户端正确存储和取回
- [ ] 单个客户端断开时，文件访问能自动故障转移
- [ ] 原有 API 接口在新架构下继续正常工作
- [ ] 文件在磁盘上以 AES-256-GCM 加密存储，无法直接读取
- [ ] 加密文件无法被直接执行（木马/脚本落地后失效）
- [ ] 下载/预览时返回解密后的原始数据，用户完全无感
- [ ] 多密钥槽机制可用，恢复口令能成功恢复 Master Key
- [ ] --export-plaintext 命令可批量导出明文文件
- [ ] 密钥管理完善（生成、加载、轮换、恢复口令）
- [ ] 旧文件可平滑迁移到加密存储
- [ ] 加密后性能损失 < 15%
- [ ] 完整的测试用例覆盖率 > 80%
- [ ] 性能不低于现有本地存储模式的 80%

---

**文档创建人**：系统规划  
**下一次评审**：功能开发启动前
