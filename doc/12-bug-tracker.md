# 踩坑记录
> 用途：记录项目开发/测试/部署过程中遇到的 Bug 和踩坑，含症状、根因、修复方案和教训
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md)、[11-deployment.md](11-deployment.md)、[13-mcp-baseline.md](13-mcp-baseline.md)、[14-dev-log.md](14-dev-log.md)

## 目录
1. [历史 Bug（7 项）](#1-历史-bug7-项)
2. [MCP 开发踩坑（7 项）](#2-mcp-开发踩坑7-项)
3. [部署踩坑（7 项）](#3-部署踩坑7-项)
4. [公开访问排查踩坑（3 项）](#4-公开访问排查踩坑3-项)
5. [教训总结](#5-教训总结)

## 1. 历史 Bug（7 项）

### Bug #1：KeyProvider.chmod 进程死锁

- **严重程度**：Critical（阻止服务启动）
- **发现阶段**：首次启动
- **症状**：`dotnet` 启动后无任何输出，进程 hang 住直到被 timeout 杀死
- **根因**：`KeyProvider.GenerateAndSaveKey()` 中调用外部 `chmod` 进程设置密钥文件权限时，同时重定向了 `RedirectStandardOutput` 和 `RedirectStandardError`，但从未读取这些管道。当管道缓冲区被填满（通常 4KB）后，子进程阻塞在 write 上，父进程阻塞在 `WaitForExit` 上，形成死锁。

**出错代码**（`FileUploadServer.Infrastructure/Encryption/KeyProvider.cs`）：
```csharp
var process = new System.Diagnostics.Process
{
    StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "chmod",
        Arguments = $"600 \"{_keyFilePath}\"",
        RedirectStandardOutput = true,   // 管道被重定向
        RedirectStandardError = true,    // 但从未读取
        UseShellExecute = false,
        CreateNoWindow = true
    }
};
process.Start();
process.WaitForExit(5000);  // 永远等不到，chmod 卡在写管道
```

- **修复**：使用 .NET 内置的 `File.SetUnixFileMode` 替代外部进程，零开销零风险。
```csharp
System.IO.File.SetUnixFileMode(filePath,
    UnixFileMode.UserRead | UnixFileMode.UserWrite);
```
- **教训**：调用外部进程时如果重定向了 stdout/stderr，**必须**读取，否则缓冲区满即死锁；.NET 已有 `File.SetUnixFileMode`，无需调用 `chmod`；所有涉及 `Process.Start` + 重定向的代码都应该检查这个模式。

---

### Bug #2：KeySlotManager 同样的 chmod 死锁

- **严重程度**：Critical
- **发现阶段**：代码审查
- **症状**：同 Bug #1
- **根因**：`KeySlotManager` 中有两处完全相同的 `chmod` 调用模式，同样的问题
- **修复**：同 Bug #1，统一改用 `File.SetUnixFileMode`
- **教训**：复制粘贴代码时，bug 也会被复制。应该抽取公共方法。

---

### Bug #3：PathMatcher 未注册 DI 容器

- **严重程度**：Critical（服务启动即崩溃）
- **发现阶段**：启动崩溃
- **症状**：
  ```
  System.InvalidOperationException: Unable to resolve service for type
  'FileUploadServer.Core.Services.PathMatcher' while attempting to activate
  'FileUploadServer.Web.Middleware.PublicFileMiddleware'.
  ```
- **根因**：`PublicFileMiddleware` 构造函数依赖 `PathMatcher`，但 `Program.cs` 中只注册了 `PublicPathOptions` 配置，忘了注册 `PathMatcher` 本身
- **修复**（`Program.cs`）：
  ```csharp
  builder.Services.Configure<PublicPathOptions>(
      builder.Configuration.GetSection("PublicPath"));
  builder.Services.AddSingleton<PathMatcher>();  // 缺失的这一行
  ```
- **教训**：中间件的构造函数参数必须全部注册到 DI；新增服务后立即尝试构建 DI 容器可以提前暴露问题。

---

### Bug #4：IPublicFileRateLimiter 未注册 DI 容器

- **严重程度**：Critical（启动后第一个请求即崩溃）
- **发现阶段**：首次请求
- **症状**：
  ```
  System.InvalidOperationException: Unable to resolve service for type
  'FileUploadServer.Web.Services.IPublicFileRateLimiter' while attempting to
  Invoke middleware 'FileUploadServer.Web.Middleware.PublicFileMiddleware'.
  ```
- **根因**：`PublicFileMiddleware.InvokeAsync` 方法参数中有 `IPublicFileRateLimiter rateLimiter`，但 `Program.cs` 只注册了具体类，未注册接口映射
- **修复**（`Program.cs`）：
  ```csharp
  builder.Services.AddSingleton<IPublicFileRateLimiter, PublicFileRateLimiter>();
  ```
- **教训**：`UseMiddleware<T>` 的 `InvokeAsync` 方法参数也是通过 DI 解析的（不仅仅是构造函数参数）；Agent 并行开发时，一个 Agent 写接口+实现，另一个 Agent 写 DI 注册，容易遗漏。

---

### Bug #5：AesGcmDecryptStream 读取密文时吞掉 AuthTag

- **严重程度**：Critical（加密文件下载全部失败）
- **发现阶段**：加密下载
- **症状**：
  ```
  System.Security.Cryptography.CryptographicException: Truncated encrypted file:
  missing authentication tag at chunk 1.
  ```
  下载返回 HTTP 500。
- **根因**：解密流 `LoadNextChunk()` 中，读取 ciphertext 的 while 循环一直读到 EOF 才停止。对于最后一个不满 `_blockSize` 的块，循环把 Nonce 之后的**所有剩余数据**（包括 AuthTag 的 16 字节）都当成了密文读走，导致后续 AuthTag 读取失败。

**加密文件格式**：
```
┌─ Header (48B) ─┬─ Chunk ──────────────────────────────┐
│ Magic/Ver/等等   │ Nonce(12B) │ Ciphertext(N B) │ Tag(16B) │
└─────────────────┴──────────────────────────────────────┘
                                                          ↑
    循环 Read(ciphertext, _, _blockSize) 会把后面的 Tag 也读走
```

- **修复**：改为一次性读取所有 rawData，再从末尾切出 AuthTag：
  ```csharp
  byte[] rawData = new byte[_blockSize + AuthTagSize];
  int rawDataRead = ReadAll(rawData);
  int ciphertextRead = rawDataRead - AuthTagSize;
  byte[] ciphertext = rawData[..ciphertextRead];
  byte[] tag = rawData[ciphertextRead..];
  ```
- **教训**：流式读取中，GCM 模式下密文=明文等长但解密时不知道明文长度；最后一个块的边界判定需显式处理，不能靠「读到 EOF 为止」；测试覆盖至少要包含空文件、<1块、=1块、>1块、N块非对齐五种场景。

---

### Bug #6：AesGcmEncryptStream.FlushAsync 未正确触发写入

- **严重程度**：Medium（部分场景下文件写入不完整）
- **发现阶段**：加密上传
- **症状**：加密上传后磁盘文件为 0 字节（目录存在但文件为空）
- **根因**：三层问题叠加：
  1. `FileApiController.Upload` 调用了 `await encryptStream.FlushAsync()`，但 `AesGcmEncryptStream` **没有重写 `FlushAsync`**，使用的是基类 `Stream.FlushAsync()` 默认实现
  2. 基类 `Stream.FlushAsync()` 调用 `Flush()` 后返回 `Task.CompletedTask`，理论上有执行 —— 但 `Flush()` 内部调用 `_innerStream.Flush()` 不一定同步刷盘
  3. `FileStream` 默认不开启 `WriteThrough`，数据可能留在 OS 缓冲区未落盘就返回
- **修复**：
  ```csharp
  // 使用 FileOptions.WriteThrough + 显式 Flush
  using (var fileStream = new FileStream(filePath, FileMode.Create,
         FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
  using (var encryptStream = new AesGcmEncryptStream(fileStream, ...))
  {
      await file.CopyToAsync(encryptStream);
      encryptStream.Flush();       // 同步刷新加密流（写文件头）
      fileStream.Flush(true);      // 强制刷盘
  }
  ```
- **教训**：`Stream.FlushAsync()` 在基类的默认实现中调用 `Flush()` 并返回 `Task.CompletedTask`，但它**不是 await FlushAsync() 能保证的**；`FileStream` 构造时使用 `FileOptions.WriteThrough` 可绕过 OS 缓冲区；写文件后应 `new FileInfo(path).Length` 验证实际写入大小。

---

### Bug #7：`dotnet publish` 残留 runtimeconfig.dev.json

- **严重程度**：Medium（导致已发布应用性能极差）
- **发现阶段**：发布部署
- **症状**：将发布后的 DLL 复制到 Linux 原生文件系统运行，启动依然极慢（>30 秒）
- **根因**：`dotnet publish` 输出目录中会生成 `*.runtimeconfig.dev.json` 文件，其中包含指向开发项目目录（`/mnt/e/...`）的路径引用。即使将发布目录复制到 ext4，运行时读取 dev.json 后仍尝试从 WSL 跨文件系统加载附加探测路径，导致编译/加载极慢
- **修复**：
  ```bash
  rm -f /opt/fileupload/*.runtimeconfig.dev.json
  ```
- **教训**：部署发布版本时必须删除 `*.runtimeconfig.dev.json`；`--sc true`（自包含）发布不受此影响（但仍建议清理）；WSL2 中 `/mnt/e/`（Windows NTFS 通过 9P 协议挂载）性能极差，所有运行时文件必须放在 Linux 原生 ext4 上。

---

## 2. MCP 开发踩坑（7 项）

来源：[14-dev-log.md](14-dev-log.md) — 2026-08-02 开发记录

| # | 踩坑 | 现象 | 解决 |
|---|------|------|------|
| 1 | **静态初始化顺序** | `ToolDefinitions.All` 声明在工具定义前，捕获到 null | `All` 移到 6 个定义之后（C# 静态字段按文本顺序初始化） |
| 2 | **静态 JsonObject 共享** | 重复序列化报 "The node already has a parent" | `ToJson()` 中 `InputSchema.DeepClone()` |
| 3 | **JsonNode.TryGetValue 泛型不存在** | `TryGetValue<T>` 只存在于 `JsonValue`，`JsonNode` 调用报错 | 先 `node is JsonValue value` 再调用 |
| 4 | **原始插值字符串与 JSON 冲突** | `$$"""` 与 JSON 大括号冲突（CS9007） | 改用普通字符串 + 转义拼接 |
| 5 | **.NET 10 multipart Content-Disposition** | `Content-Disposition: form-data; name=path` **不带引号**，断言用了带引号格式 | 断言改为 `name=path` |
| 6 | **文档自相矛盾** | RETRY-02 期望 503 -> -32001，ERR-05 期望 503 -> -32005 | 采用语义正确的 -32005（服务不可用） |
| 7 | **HttpUtility 不可用** | `HttpUtility.ParseQueryString` 属 System.Web，.NET Core 不可用 | 改用 URL 字符串拼接 |

## 3. 部署踩坑（7 项）

来源：[14-dev-log.md](14-dev-log.md) — 2026-08-02 部署记录

| # | 踩坑 | 现象 | 解决 |
|---|------|------|------|
| 1 | **pkill -f 误杀 SSH 会话** | `pkill -f 'FileUploadServer.Web'` 匹配到 ssh 命令行自身 → 连接断开（exit 255） | 用 `pgrep -f '\./FileUploadServer\.Web'` 取精确 PID 再 kill |
| 2 | **text file busy** | 运行中的可执行文件无法覆盖（scp 失败） | 先停进程再上传 |
| 3 | **nohup 挂起 ssh** | nohup 启动的子进程持有 ssh 管道 → 命令超时挂起 | 启动命令与验证命令分离执行 |
| 4 | **WS secret 不匹配** | 认证 token 计算中服务端用 `ClientSecretHash`，客户端需先 SHA256(secret)；实际是旧 secret 哈希与库不符 | 用 `regenerate-secret` 重新生成密钥 |
| 5 | **多进程争抢连接** | 3 个同 id WsClient 反复 `Disconnected: Connection lost` | 清理为单实例 |
| 6 | **sshpass 不可用** | 密码认证无 sshpass（WSL 环境未安装） | `SSH_ASKPASS` + `setsid` 方式 |
| 7 | **沙箱管道丢环境变量** | Bash 工具管道中内联环境变量丢失 | 用输入重定向 `< file` 代替管道 |

## 4. 公开访问排查踩坑（6 项）

来源：[14-dev-log.md](14-dev-log.md) — 2026-08-02 排查记录

| # | 踩坑 | 现象 | 解决 |
|---|------|------|------|
| 1 | **StartsWithSegments("/p/") 不匹配** | `PathString.StartsWithSegments("/p/")` 对 `/p/public/...` 返回 **False**（实测），导致 API Key 中间件对公开路径起了鉴权 | 定位到 `ApiKeyAuthMiddleware:27` 既有 bug，此前被 PublicFileMiddleware 屏蔽掩盖 |
| 2 | **老文件 tag mismatch** | 新上传文件公开访问解密成功（说明解密逻辑正确），但 7-11 上传的老文件解密失败（`CryptographicException: tag mismatch`） | 老密文与当前密钥不匹配（数据层问题），改代码无法修复，只能重传 |
| 3 | **本地解密验证 vs 网关结果矛盾** | 本地用密钥文件解密失败，网关公开访问新文件却成功 — 看似矛盾 | 最终确认：网关用密钥文件，本地验证方法正确；老文件确实无法解密，新文件正常 |
| 4 | **三个下载入口解密不一致** | 网页下载正常、MCP 下载返回 `FUEC` 密文、公共访问解密异常（三处都指向 WS 节点） | 根因：解密逻辑未统一，`FileApiController.Download` 的 WS 分支漏解密；新建 `FileDownloadService` 统一三入口解密修复 |
| 5 | **网页删除不清理 WS 节点文件** | 网页删除后数据库记录没了，但 WS 节点密文永久残留（只删了本地 `StoredFileName`） | 根因：`Index.cshtml.cs` 删除逻辑缺失 WS 远程 + 加密子目录 + FileLocation 清理；新建 `FileDeleteService` 统一修复 |
| 6 | **上传本地副本残留** | 网关本地 `wwwroot/uploads` 累积 16 个无记录对应的孤儿密文（上传时本地加密副本从不删除） | 根因：上传流程本地加密中转副本在 WS 转发成功后未删除；修复为 WS 转发成功即删本地副本（API + 网页两处），存量孤儿已手动清理 |

## 5. 教训总结

### 5.1 核心教训

1. **多 Agent 并行开发的最大风险是 DI 注册遗漏**（Bug #3、#4）。Agent 各自创建接口+实现+中间件，但 DI 注册在共享的 `Program.cs` 中，容易遗漏。建议：新增服务后立即写 DI 注册测试。

2. **流式加密/解密边界条件必须充分测试**（Bug #5、#6）。至少要测试空文件、<1块、=1块边界、多块大文件四种场景。

3. **永远不要用 `Process.Start` + `RedirectStandardOutput/Error` 但不读取**（Bug #1、#2）。.NET 内置 API 已经覆盖了常见场景。

4. **WSL2 开发环境下注意文件系统性能**（Bug #7）。务必使用 `--sc true` 自包含发布或确保运行时文件在 ext4 上。

5. **分层架构：中间件不绕过 API 层直接操作存储策略**（公开访问踩坑）。PublicFileMiddleware 直接连 WS 节点违背分层，且对加密文件解密失败。

6. **数据 vs 代码问题要分清**（公开访问踩坑）。老密文无法解密是数据问题，改代码无效，需重传文件。

### 5.2 类型分布

| 类别 | Bug 数 |
|------|--------|
| DI 配置遗漏 | 2 |
| 进程管理/IO | 2 |
| 流式加密算法 | 2 |
| 部署配置 | 1 |

### 5.3 发现阶段分布

| 阶段 | Bug 数 |
|------|--------|
| 首次启动 | 2 |
| 启动崩溃 | 1 |
| 首次请求 | 1 |
| 功能测试 | 2 |
| 发布部署 | 1 |

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览（中间件管线）
- [04-encryption.md](04-encryption.md) — 文件存储加密细案（AesGcmEncryptStream/AesGcmDecryptStream 实现）
- [11-deployment.md](11-deployment.md) — 部署运维指南（含部署踩坑）
- [13-mcp-baseline.md](13-mcp-baseline.md) — MCP 开发规范
- [14-dev-log.md](14-dev-log.md) — 开发日志（踩坑原文出处）
