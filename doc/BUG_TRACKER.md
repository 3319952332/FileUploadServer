# FileUploadServer 踩坑合集

**创建时间**：2026-07-10  
**关联文档**：IMPLEMENTATION_PLAN.md / IMPLEMENTATION_PLAN_NETWORK.md / IMPLEMENTATION_PLAN_CLIENTS.md

---

## Bug #1：KeyProvider.chmod 进程死锁导致服务无法启动

**严重程度**：🔴 Critical（阻止服务启动）

**症状**：`dotnet` 启动后无任何输出，进程 hang 住直到被 timeout 杀死。

**根因**：`KeyProvider.GenerateAndSaveKey()` 中调用外部 `chmod` 进程设置密钥文件权限时，同时重定向了 `RedirectStandardOutput` 和 `RedirectStandardError`，但从未读取这些管道。当管道缓冲区被填满（通常 4KB）后，子进程阻塞在 write 上，父进程阻塞在 `WaitForExit` 上，形成死锁。

**出错代码**（`FileUploadServer.Infrastructure/Encryption/KeyProvider.cs`）：
```csharp
var process = new System.Diagnostics.Process
{
    StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "chmod",
        Arguments = $"600 \"{_keyFilePath}\"",
        RedirectStandardOutput = true,   // ← 管道被重定向
        RedirectStandardError = true,    // ← 但从未读取！
        UseShellExecute = false,
        CreateNoWindow = true
    }
};
process.Start();
process.WaitForExit(5000);  // ← 永远等不到，chmod 卡在写管道
```

**修复**：使用 .NET 内置的 `File.SetUnixFileMode` 替代外部进程，零开销零风险。

```csharp
System.IO.File.SetUnixFileMode(filePath,
    UnixFileMode.UserRead | UnixFileMode.UserWrite);
```

**教训**：
- 调用外部进程时，如果重定向了 stdout/stderr，**必须**读取，否则缓冲区满即死锁
- .NET 已有 `File.SetUnixFileMode`，无需 `chmod`
- 所有涉及 `Process.Start` + 重定向的代码都应该检查这个模式

---

## Bug #2：KeySlotManager 同样的 chmod 死锁

**严重程度**：🔴 Critical

**症状**：同上。

**根因**：`KeySlotManager` 中有两处完全相同的 `chmod` 调用模式，同样的问题。

**修复**：同 Bug #1，统一改用 `File.SetUnixFileMode`。

**教训**：复制粘贴代码时，bug 也会被复制。应该抽取公共方法。

---

## Bug #3：PathMatcher 未注册 DI 容器

**严重程度**：🔴 Critical（服务启动即崩溃）

**症状**：
```
System.InvalidOperationException: Unable to resolve service for type
'FileUploadServer.Core.Services.PathMatcher' while attempting to activate
'FileUploadServer.Web.Middleware.PublicFileMiddleware'.
```

**根因**：`PublicFileMiddleware` 构造函数依赖 `PathMatcher`，但 `Program.cs` 中只注册了 `PublicPathOptions` 配置，忘了注册 `PathMatcher` 本身。

**修复**（`Program.cs`）：
```csharp
builder.Services.Configure<PublicPathOptions>(
    builder.Configuration.GetSection("PublicPath"));
builder.Services.AddSingleton<PathMatcher>();  // ← 缺失的这一行
```

**教训**：
- 中间件的构造函数参数必须全部注册到 DI
- 新增服务后立即尝试构建 DI 容器可以提前暴露问题
- 使用 `builder.Services.BuildServiceProvider().GetRequiredService<T>()` 可以做快速 DI 校验

---

## Bug #4：IPublicFileRateLimiter 未注册 DI 容器

**严重程度**：🔴 Critical（启动后第一个请求即崩溃）

**症状**：
```
System.InvalidOperationException: Unable to resolve service for type
'FileUploadServer.Web.Services.IPublicFileRateLimiter' while attempting to
Invoke middleware 'FileUploadServer.Web.Middleware.PublicFileMiddleware'.
```

**根因**：`PublicFileMiddleware.InvokeAsync` 方法参数中有 `IPublicFileRateLimiter rateLimiter`，但 Program.cs 只注册了具体类，未注册接口映射。

**修复**（`Program.cs`）：
```csharp
builder.Services.AddSingleton<IPublicFileRateLimiter, PublicFileRateLimiter>();
```

**教训**：
- `UseMiddleware<T>` 的 `InvokeAsync` 方法参数也是通过 DI 解析的（不仅仅是构造函数参数）
- 从 ServiceProvider 解析的参数可以比构造函数参数更多，但都必须注册
- Agent 并行开发时，一个 Agent 写接口+实现，另一个 Agent 写 DI 注册，容易遗漏

---

## Bug #5：AesGcmDecryptStream 读取密文时吞掉 AuthTag

**严重程度**：🔴 Critical（加密文件下载全部失败）

**症状**：
```
System.Security.Cryptography.CryptographicException: Truncated encrypted file:
missing authentication tag at chunk 1.
```
下载返回 HTTP 500。

**根因**：解密流 `LoadNextChunk()` 方法中，读取 ciphertext 的 while 循环会一直读到 EOF 才停止。对于最后一个不满 `_blockSize` 的块，循环把 Nonce 之后的**所有剩余数据**（包括 AuthTag 的 16 字节）都当成了密文读走，导致后续的 AuthTag 读取失败。

**加密文件格式**：
```
┌─ Header (48B) ─┬─ Chunk ──────────────────────────────┐
│ Magic/Ver/等等   │ Nonce(12B) │ Ciphertext(N B) │ Tag(16B) │
└─────────────────┴──────────────────────────────────────┘
                                                          ↑
    循环 Read(ciphertext, _, _blockSize) 会把后面的 Tag 也读走
```

**出错代码**（`AesGcmChunkedStream.cs`）：
```csharp
// 原来的逻辑：ciphertext 和 tag 分开读取
// 问题：ciphertext 循环读到 EOF，连 tag 一起吞了
byte[] ciphertext = new byte[_blockSize];
while (remaining > 0) {
    int chunkRead = _innerStream.Read(ciphertext, ...);
    if (chunkRead == 0) break;
    ciphertextRead += chunkRead;
}
byte[] tag = new byte[16];
_innerStream.Read(tag, 0, 16);  // ← 已经 EOF，读不到
```

**修复**：改为一次性读取所有 rawData，再从末尾切出 AuthTag。
```csharp
// 把 nonce 之后的数据全读出来
byte[] rawData = new byte[_blockSize + AuthTagSize];
int rawDataRead = ReadAll(rawData);

// 最后 16 字节 = AuthTag，其余 = Ciphertext
int ciphertextRead = rawDataRead - AuthTagSize;
byte[] ciphertext = rawData[..ciphertextRead];
byte[] tag = rawData[ciphertextRead..];
```

**教训**：
- 流式读取中，GCM 模式下密文=明文等长，但解密时不知道明文长度
- 最后一个块的边界判定需要显式处理，不能靠"读到 EOF 为止"
- 这种 bug 只有在文件不满一个 block 大小时才触发，多块文件可能碰巧通过
- 测试覆盖至少要包含：空文件、<1块、=1块、>1块、N块非对齐

---

## Bug #6：AesGcmEncryptStream.FlushAsync 未正确触发写入

**严重程度**：🟡 Medium（部分场景下文件写入不完整）

**症状**：加密上传后磁盘文件为 0 字节（目录存在但文件为空）。

**根因**：三层问题叠加：

1. `FileApiController.Upload` 调用了 `await encryptStream.FlushAsync()`，但 `AesGcmEncryptStream` **没有重写 `FlushAsync`**，使用的是基类 `Stream.FlushAsync()` 默认实现
2. 基类 `Stream.FlushAsync()` 调用 `Flush()` 后返回 `Task.CompletedTask`，理论上有执行 —— 但 `Flush()` 内部调用 `_innerStream.Flush()` 不一定同步刷盘
3. `FileStream` 默认不开启 `WriteThrough`，数据可能留在 OS 缓冲区未落盘就返回

**修复**：
```csharp
// 修改前
using var fileStream = new FileStream(filePath, FileMode.Create);
using var encryptStream = new AesGcmEncryptStream(fileStream, ...);
await file.CopyToAsync(encryptStream);
await encryptStream.FlushAsync();  // ← 不可靠

// 修复后
using (var fileStream = new FileStream(filePath, FileMode.Create,
       FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
using (var encryptStream = new AesGcmEncryptStream(fileStream, ...))
{
    await file.CopyToAsync(encryptStream);
    encryptStream.Flush();       // 同步刷新加密流（写文件头）
    fileStream.Flush(true);      // 强制刷盘
}
```

**教训**：
- `Stream.FlushAsync()` 在基类的默认实现中调用 `Flush()` 并返回 `Task.CompletedTask`，但它**不是 `await FlushAsync()` 能保证的**
- `FileStream` 构造时使用 `FileOptions.WriteThrough` 可绕过 OS 缓冲区
- `using var`（C# 8.0+）的隐式作用域在使用流时不如显式 `using` 块清晰
- 写文件后应该 `new FileInfo(path).Length` 验证实际写入大小

---

## Bug #7：`dotnet publish` 残留 runtimeconfig.dev.json

**严重程度**：🟡 Medium（导致已发布应用性能极差）

**症状**：将发布后的 DLL 复制到 Linux 原生文件系统运行，启动依然极慢（>30 秒）。

**根因**：`dotnet publish` 输出目录中会生成 `*.runtimeconfig.dev.json` 文件，其中包含指向开发项目目录（`/mnt/e/...`）的路径引用。即使将发布目录复制到 `/home/wang/fus-sc`（ext4），运行时读取 dev.json 后仍会尝试从 WSL 跨文件系统加载附加探测路径，导致编译/加载极慢。

**修复**：
```bash
rm -f /home/wang/fus-sc/*.runtimeconfig.dev.json
```

**教训**：
- 部署发布版本时必须删除 `*.runtimeconfig.dev.json`
- `--sc true`（自包含）发布不受此影响（但仍建议清理）
- WSL2 中 `/mnt/e/`（Windows NTFS 通过 9P 协议挂载）性能极差，所有运行时文件必须放在 Linux 原生 ext4 上

---

## 总结

| # | Bug | 类别 | 发现阶段 |
|---|-----|------|----------|
| 1 | `chmod` 管道死锁 | 进程管理 | 首次启动 |
| 2 | 复制的 `chmod` 死锁 | 进程管理 | 代码审查 |
| 3 | `PathMatcher` 未注册 DI | DI 配置 | 启动崩溃 |
| 4 | `IPublicFileRateLimiter` 未注册 DI | DI 配置 | 首次请求 |
| 5 | 解密流吞 AuthTag | 算法逻辑 | 加密下载 |
| 6 | 加密流未刷盘 | IO/流管理 | 加密上传 |
| 7 | dev runtimeconfig 残留 | 部署/性能 | 发布部署 |

**核心教训**：

1. **多 Agent 并行开发的最大风险是 DI 注册遗漏**（Bug #3、#4）。Agent 各自创建接口+实现+中间件，但 DI 注册在共享的 `Program.cs` 中，容易遗漏。建议：新增服务后立即写 DI 注册测试。

2. **流式加密/解密边界条件必须充分测试**（Bug #5、#6）。至少要测试空文件、<1块、=1块边界、多块大文件四种场景。

3. **永远不要用 `Process.Start` + `RedirectStandardOutput/Error` 但不读取**（Bug #1、#2）。.NET 内置 API 已经覆盖了常见场景。

4. **WSL2 开发环境下注意文件系统性能**（Bug #7）。务必使用 `--sc true` 自包含发布或确保运行时文件在 ext4 上。
