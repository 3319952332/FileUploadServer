# MCP 编码缺陷 & 网关 ACL 冲突 交接文档

> 用途：交接 2026-09-18 排查「MCP 无法把文件设为公开 + 错误信息中文乱码」的全过程、已完成的改动、待办事项与可复现验证手册
> 创建：2026-09-18 | 关联：[08-mcp.md](08-mcp.md)、[06-public-access.md](06-public-access.md)、[12-bug-tracker.md](12-bug-tracker.md)、[13-mcp-baseline.md](13-mcp-baseline.md)、[11-deployment.md](11-deployment.md)

> ⚠️ **本文档不含任何真实密钥**。涉及 Admin Key 处一律用 `<AdminKey>` 占位，实际取值见 MCP 配置的环境变量 `FILE_SERVER_MASTER_KEY`，或通过 SSH 在网关本机查询 `GET http://localhost:7000/api/admin/keys`。

---

## 目录

1. [结论摘要](#1-结论摘要)
2. [环境事实](#2-环境事实)
3. [问题一：file_set_public 与 nginx ACL 结构性冲突](#3-问题一file_set_public-与-nginx-acl-结构性冲突)
4. [问题二：ErrorMapper 把网关 403 误报为「密钥无权限」](#4-问题二errormapper-把网关-403-误报为密钥无权限)
5. [问题三：stdio 中文乱码（已修，待生效）](#5-问题三stdio-中文乱码已修待生效)
6. [问题四：3 个既存单测失败（Windows 路径）](#6-问题四3-个既存单测失败windows-路径)
7. [已完成动作与验证证据](#7-已完成动作与验证证据)
8. [待办清单](#8-待办清单)
9. [复现与验证手册](#9-复现与验证手册)
10. [遗留风险与注意事项](#10-遗留风险与注意事项)
11. [关联文档](#11-关联文档)

---

## 1. 结论摘要

**起因**：DSH 侧生成图片上传至服务器后，调用 MCP `file_set_public` 报错 `-32003 权限不足：当前密钥无权访问该资源`，且返回的 `https://file.sub.opengm.top/p/public/xxx.png` 链接不可访问（404/403）。

**三条结论**：

| # | 结论 | 性质 |
|---|------|------|
| 1 | 网关**健康无损**。403 来自 nginx 对 `/api/admin/*` 的**设计内 ACL**（`deny all; return 403`），与密钥权限**无关** | 非缺陷，是设计 |
| 2 | `file_set_public` 是 6 个 MCP 工具中**唯一**走 admin 路由的，而 MCP Server 跑在客户端、走公网 —— 从公网调用**必然 403** | **架构缺口**，待决策 |
| 3 | 错误信息中文乱码（`Ȩ�޲���`）是 stdio 编码缺陷：服务端吐 GBK 字节、客户端按 UTF-8 读 | **缺陷**，已修待生效 |

**已完成的补救**：3 张图片已改走「服务器本机 localhost:7000 直连」这一配置指定的运维路径完成发布，`/p/public/*` 链接全部 200 且字节数逐一对得上。

---

## 2. 环境事实

| 项 | 值 |
|---|---|
| 网关 | `111.229.53.125`（`ubuntu`，密钥 `~/.ssh/CouldServer_1.pem`） |
| 公网域名 | `https://file.sub.opengm.top` |
| 应用 | `FileUploadServer.Web`，监听 `0.0.0.0:7000` |
| nginx 配置 | `/etc/nginx/conf.d/*.conf` 中 `server_name file.sub.opengm.top` 块 |
| WS 存储节点 | `192.168.1.4`（`laowang`），数据在 `/home/laowang/wsdata/` |
| MCP Server 运行位置 | **客户端机器**（非服务器），stdio 传输，Release dll：`FileUploadServer.Mcp/bin/Release/net10.0/FileUploadServer.Mcp.dll` |
| MCP 鉴权 | 环境变量 `FILE_SERVER_MASTER_KEY`（Admin 型密钥） |
| MCP 后端地址 | 环境变量 `FILE_SERVER_BASE_URL=https://file.sub.opengm.top`（**公网**） |

**nginx 关键配置原文**：

```nginx
server {
    server_name file.sub.opengm.top;

    # 管理接口仅允许服务器本机访问（登录服务器/运维走 localhost:7000 直连）
    location ^~ /api/admin/ {
        deny all;
        return 403;
    }

    location / {
        proxy_pass http://localhost:7000;
        # ...
    }
}
```

> 注意注释里的既定意图：**admin 接口的运维路径就是「本机 localhost:7000 直连」**。这为问题一提供了官方解法。

---

## 3. 问题一：`file_set_public` 与 nginx ACL 结构性冲突

### 3.1 症状

```
MCP file_set_public(file_id=336, is_public=true, public_path="/public/xxx.png")
→ {"status":"error","error_code":-32003,
   "message":"权限不足：当前密钥无权访问该资源（file_set_public id=336）<html>...403 Forbidden...nginx/1.24.0 (Ubuntu)...</html>"}
```

### 3.2 证据链（三证互印）

| 证据 | 内容 |
|---|---|
| nginx 访问日志 | 全日志 `403` **仅 2 条**，均为 `PUT /api/admin/files/336/public?key=***` |
| nginx 配置 | `location ^~ /api/admin/ { deny all; return 403; }` |
| 应用 swagger | `/api/admin/files/{id}/public` 路由**确实存在**，`SetPublicRequest { isPublic: bool, publicPath: string? }` |

**请求行原文**（来自 `/var/log/nginx/file.sub.opengm.top.access.log`）：

```
&lt;client-ip&gt; - - [18/Sep/2026:19:34:22 +0800] "PUT /api/admin/files/336/public?key=*** HTTP/1.1" 403 162
```

→ 请求在 **nginx 层**即被拒（响应体 162 字节＝nginx 错误页），**根本没到达应用**。

### 3.3 根因：工具按「应用 API 面」设计，却部署在「只开放公开路由」的网络边界

`FileUploadServer.Mcp/Services/FileToolHandlers.cs` 中 6 个工具的路由映射：

| 工具 | 路由 | 公网可达 |
|---|---|---|
| `file_list` | `GET /api/files` | ✅ |
| `file_info` | `GET /api/files/{id}` | ✅ |
| `file_upload` | `POST /api/files` | ✅ |
| `file_download` | `GET /api/files/download/{id}` | ✅ |
| `file_delete` | `DELETE /api/files/{id}` | ✅ |
| **`file_set_public`** | **`PUT /api/admin/files/{id}/public`** | ❌ **nginx `deny all`** |

（源码位置：`FileToolHandlers.cs:40 / :50 / :77 / :88 / :118 / :159`）

**两层各自都合理，是中间没人对齐**：

1. **应用层**：发布文件确实改变全局匿名访问状态，属管理动作 → 放 `/api/admin/*` 是合理设计（见 [06-public-access.md](06-public-access.md) §7.3 把它列为官方端点）。
2. **部署层**：MCP Server 跑在客户端、用**公网** base URL 调网关，而 nginx 把整个 `/api/admin/` 对公网关闭。

**旁证（说明这不是偶然）**：[skill `file-upload-server-mcp`](../../.claude/skills/file-upload-server-mcp) 的排错表把 `-32003 权限不足` 归因为「Master Key 无效/过期/被删除 → 重新获取 Admin 密钥」——**文档给的诊断方向本身就是错的**，与本次误判同源。

### 3.4 可选解法（待决策，四选一）

| 方案 | 做法 | 优点 | 代价 |
|---|---|---|---|
| **A. 走本机发布**（当前采用） | 需要发布时 SSH 到网关执行 `PUT http://localhost:7000/api/admin/files/{id}/public` | 零改动、**不降低安全边界** | 每次多一跳 SSH；远程 MCP 客户端无法自助发布 |
| B. nginx 精确放行 | 仅放行 `PUT /api/admin/files/{id}/public`，其余 admin 继续 deny | 一劳永逸 | 把一条**写接口**暴露到公网，仅靠 key 拦 |
| C. 应用新增公开鉴权接口 | 如 `PUT /api/files/{id}/public`，走普通 key 鉴权 | 最规范 | 需改 .NET 代码 + 重新部署 |
| D. MCP 侧走隧道 | MCP Server 内改为通过 SSH 隧道调 localhost:7000 | 客户端自助 | 需引入 SSH/隧道依赖，复杂度高 |

> **推荐 A**（零风险、与 nginx 注释的既定运维路径一致）；若追求 MCP 工具可用性，选 C。

---

## 4. 问题二：ErrorMapper 把网关 403 误报为「密钥无权限」

### 4.1 问题代码

`FileUploadServer.Mcp/Services/ErrorMapper.cs`：

```csharp
// :69  —— 403 被无差别映射为「权限不足」
401 or 403 => JsonRpcError.Codes.PermissionDenied,     // -32003

// :84  —— 文案直接断言是密钥权限问题
403 => $"权限不足：当前密钥无权访问该资源{hint}",

// :91-93  —— 把后端 body 原样拼进 message（把 nginx 整页 HTML 也带进来了）
if (!string.IsNullOrEmpty(body) && body != baseMessage)
    return $"{baseMessage}：{body}";
```

### 4.2 为什么这是缺陷

**403 的两种语义被合并成一个**：

| 403 来源 | 真实含义 | 应报内容 |
|---|---|---|
| 应用层 | 密钥确实无权限 | ✅ `-32003 权限不足` |
| **网关层** | nginx ACL `deny all`，**与密钥无关** | ❌ 应报「该接口不对公网开放，请走服务器本机 localhost:7000」 |

后果：错误的诊断信息直接把排查方向带偏到「密钥」上（本次误判即由此产生，且与 §3.3 所述 skill 文档的错误归因一致）。

### 4.3 建议修法

1. **区分网关拒绝**：当 `body` 含 `<html` / `nginx/` 时判定为网关层拒绝，改用明确文案（如「该接口不允许从公网访问（网关 ACL），请在服务器本机通过 localhost:7000 调用」）。
2. **不再原样透传 HTML**：剥离/截断标签后再拼接，避免错误信息里混入整页 HTML。
3. **同步修正 skill 文档**（`.claude/skills/file-upload-server-mcp`）中 `-32003` 的归因条目。

---

## 5. 问题三：stdio 中文乱码（已修，待生效）

### 5.1 症状

错误信息中文乱码：`Ȩ�޲���：��ǰ��Կ��Ȩ���ʸ���Դ��`（应为「权限不足：当前密钥无权访问该资源」）。

### 5.2 根因（已精确验证）

- `Protocol/McpJson.cs:14` 使用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` → **中文不转义、原样输出**。
- `Program.cs` **未设置任何输出编码** → Windows 下 `Console.Out` 沿用控制台代码页（GBK/936）写 stdout。
- 客户端按 **UTF-8** 解码这些 **GBK 字节** → 乱码。

**乱码字符可反推印证**：`Ȩ` = U+0228，正是 GBK 的「权」(`C8 A8`) 被当 UTF-8 解码的结果（`C8` 是双字节前导、`A8` 是合法续字节）；「限」的 GBK `CF DE` 中 `DE` 非续字节 → 变成 `�`(U+FFFD)。

### 5.3 已实施改动（`FileUploadServer.Mcp/Program.cs`，+16/-1，**尚未提交**）

```csharp
// stdio 编码：协议流与日志流统一强制 UTF-8。
// McpJson 使用 UnsafeRelaxedJsonEscaping（中文原样输出、不转义为 \uXXXX），
// 若沿用 Windows 默认代码页（GBK/936）写 stdout，中文会被按 UTF-8 解码的客户端
// 读成乱码（如 "权限不足" → "Ȩ�޲���"）。
// 这里不用 Console.OutputEncoding：stdout 被重定向为管道时进程可能没有控制台
// 句柄，该 setter 会抛 IOException；直接包装标准流则与是否有控制台无关。
var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var standardInput  = new StreamReader(Console.OpenStandardInput(), utf8NoBom);
using var standardOutput = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom);
using var standardError  = new StreamWriter(Console.OpenStandardError(), utf8NoBom) { AutoFlush = true };
McpLogger.Writer = msg => standardError.WriteLine(msg);

// 原：await using var transport = new StdioTransport();
await using var transport = new StdioTransport(standardInput, standardOutput);
```

**设计要点**：`StdioTransport` 构造函数本就支持注入 reader/writer（原为可测试性设计），此处直接利用，无需改传输层。

> ⚠️ **不要改用 `Console.OutputEncoding = ...`**：MCP 的 stdout 是管道，进程可能没有控制台句柄，该 setter 会抛 `IOException`。

### 5.4 验证证据（A/B 对照，已实测通过）

同一输入、同样 `chcp 936` 控制台，把 stdout 按**原始字节**以 UTF-8 解码：

| 版本 | 响应 message | 判定 |
|---|---|---|
| **修复前** | `"ȱ�ٱ������: is_public"` | ❌ 含 U+FFFD，非正确中文 |
| **修复后** | `"缺少必填参数: is_public"` | ✅ 正确 |

（复现步骤见 §9.4）

### 5.5 生效步骤（重要）

当前 MCP 进程**正锁着 Release dll**，因此必须按序操作：

```powershell
# 1. 找到并停止当前 MCP 进程（会同时断开 DSH 的 MCP 工具，属预期）
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -match 'FileUploadServer\.Mcp' } |
  Select-Object ProcessId, CommandLine

# 2. 重新构建 Release（此时文件不再被占用）
dotnet build E:\Code\FileUploadServer\FileUploadServer.Mcp\FileUploadServer.Mcp.csproj -c Release

# 3. 重启 DSH / 重开会话，使其重新拉起 MCP Server
```

**验证生效**：随便触发一次中文错误（如调用 `file_set_public` 但省略 `is_public`），确认返回「缺少必填参数: is_public」而非乱码。

---

## 6. 问题四：3 个既存单测失败（Windows 路径）

### 6.1 现象

```
dotnet test FileUploadServer.Tests/FileUploadServer.Tests.csproj -c Debug --filter "FullyQualifiedName~Mcp"
→ 失败: 3，通过: 48，总计: 51
```

失败用例（全部为 upload 路径）：

1. `McpFileUploadTests.FileUpload_ReturnsMetadata_OnSuccess`
2. `McpFileUploadTests.FileUpload_WithRemotePath_SendsPathFormField`
3. `McpFileUploadTests.FileUpload_TooLarge_ReturnsError`

### 6.2 根因：Windows 路径反斜杠塞进手拼 JSON

`FileUploadServer.Tests/Mcp/TestHelpers/FakeMcpServer.cs:68-73`：

```csharp
private static JsonRpcRequest MakeRequest(long? id, string method, string paramsJson = "{}")
{
    var json = $"{{\"jsonrpc\":\"2.0\",{idPart}\"method\":\"{method}\",\"params\":{paramsJson}}}";
    return JsonRpcRequest.TryParse(json, out _)!;   // ← 解析失败返回 null，! 把警告压掉
}
```

用例侧（如 `McpFileUploadTests.cs:75-76`、`:102`）把 **Windows 临时路径**直接插值进 JSON：

```csharp
await fake.CallToolAsync("file_upload",
    $"{{\"local_file_path\":\"{localPath}\",\"remote_path\":\"/docs/report.pdf\"}}");
// localPath = C:\Users\...\Temp\xxx.txt  →  \U \w 等非法 JSON 转义
```

→ `TryParse` 返回 `null` → `!` 抑制警告 → 直到 `McpServer.HandleAsync` 的 `request.IsNotification` 才抛 `NullReferenceException`。

**为何 CI 全绿**：Linux 临时路径形如 `/tmp/xxx.txt`，无反斜杠，JSON 合法 → **该测试集是 Windows 不兼容的**。

### 6.3 已证实为既存问题（非本次改动引入）

用 `git stash` 摘掉 §5.3 的改动后，**同样 3 失败 / 48 通过**，结果完全一致。

### 6.4 建议修法

把 `CallToolAsync` / `MakeRequest` 的手拼 JSON 改为序列化构造，从根上消除转义问题：

```csharp
public async Task<JsonRpcResponse> CallToolAsync(string name, JsonObject? args = null, long id = 10)
{
    var payload = new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() };
    var request = MakeRequest(id, "tools/call", payload);
    return (await Server.HandleAsync(request))!;
}

private static JsonRpcRequest MakeRequest(long? id, string method, JsonNode? paramsNode = null)
{
    var obj = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = paramsNode ?? new JsonObject() };
    if (id.HasValue) obj["id"] = id.Value;
    return JsonRpcRequest.TryParse(obj.ToJsonString(McpJson.SerializeOptions), out _)
           ?? throw new InvalidOperationException($"测试构造的 JSON-RPC 请求无法解析: {method}");
}
```

同时建议 `MakeRequest` 解析失败时**显式抛异常**，而不是靠 `!` 吞掉 —— 让此类问题在构造点立刻暴露。

---

## 7. 已完成动作与验证证据

### 7.1 图片发布（走本机正确路径）

对 3 个文件通过 **localhost:7000**（而非公网）执行发布，均返回 `HTTP 200`、`isPublic: true`：

| 文件 ID | 文件名 | publicPath | 公网链接 | 验证结果 |
|---|---|---|---|---|
| 334 | `dsh-qwen-img-1789646812869-93ewmp.png` | `/public/dsh-qwen-img-1789646812869-93ewmp.png` | https://file.sub.opengm.top/p/public/dsh-qwen-img-1789646812869-93ewmp.png | `200` / 2208495 B / `image/png` |
| 335 | `dsh-qwen-img-1789648002673-1coav3.png` | `/public/dsh-qwen-img-1789648002673-1coav3.png` | https://file.sub.opengm.top/p/public/dsh-qwen-img-1789648002673-1coav3.png | `200` / 1799735 B / `image/png` |
| 336 | `dsh-qwen-img-1789731260655-h89vn7.png` | `/public/dsh-qwen-img-1789731260655-h89vn7.png` | https://file.sub.opengm.top/p/public/dsh-qwen-img-1789731260655-h89vn7.png | `200` / 1687469 B / `image/png` |

> 字节数与 `fileSize` 逐一对得上，非缓存命中。3 个文件均存储在 **WS 节点**（`storageMode: WebSocket`），说明 `/p/` 的 WS 读取 + 解密链路工作正常。

### 7.2 与既有设计的符合性

`06-public-access.md` §1 确认 `/p/` 公开访问**已启用**；§7.3 将 `PUT /api/admin/files/{id}/public` 列为官方端点。故本次发布**符合工程既定设计**（`14-dev-log.md` 中「屏蔽公开访问」是已被 §7.2 推翻的旧决策）。

### 7.3 工作区状态

- 代码改动：`FileUploadServer.Mcp/Program.cs`，**1 file changed, +16/-1**，**未提交**。
- 已验证后清理的临时产物：`publish/mcp-verify/`（验证用构建输出）、`%TEMP%\mcptest\`、备份文件等。

---

## 8. 待办清单

| 优先级 | 事项 | 位置 | 动作 |
|---|---|---|---|
| **P0** | 构建 Release 并使编码修复生效 | `FileUploadServer.Mcp/Program.cs` | 见 §5.5 三步（停 MCP → 重建 → 重启 DSH） |
| **P0** | 提交编码修复 | git | `git add FileUploadServer.Mcp/Program.cs && git commit` |
| **P1** | 修正 403 语义误映射 | `Services/ErrorMapper.cs:69,84,91` | 见 §4.3 |
| **P1** | 修复 3 个 Windows 单测失败 | `Tests/Mcp/TestHelpers/FakeMcpServer.cs:68` 等 | 见 §6.4 |
| **P2** | 决策 `file_set_public` 架构方案 | `FileToolHandlers.cs:159` | 见 §3.4（推荐 A 或 C） |
| **P2** | 修正 skill 文档的错误归因 | `.claude/skills/file-upload-server-mcp` | `-32003` 条目补充网关 ACL 场景 |
| **P2** | 待办完成后合并入文档 | `12-bug-tracker.md` / `14-dev-log.md` | 本文件可归档 |

---

## 9. 复现与验证手册

### 9.1 查看网关 ACL

```powershell
ssh 111.229.53.125 'cat /etc/nginx/conf.d/*.conf | grep -A5 "file.sub.opengm.top"'
```

### 9.2 查看 admin 端点契约（swagger，仅本机）

```powershell
ssh 111.229.53.125 'curl -s http://localhost:7000/swagger/v1/swagger.json | python3 -m json.tool | grep -A45 "\"/api/admin/files/{id}/public\""'
```

### 9.3 走本机发布（当前采用的正确姿势）

```powershell
ssh 111.229.53.125 '
KEY=<AdminKey>
curl -s -X PUT "http://localhost:7000/api/admin/files/<文件ID>/public?key=$KEY" \
  -H "Content-Type: application/json" \
  -d "{\"isPublic\":true,\"publicPath\":\"/public/<文件名>\"}"
'
```

### 9.4 复现 stdio 乱码 A/B（关键：必须给它 936 代码页的控制台）

```powershell
# 1) 准备 NDJSON 输入（三条：initialize / initialized / 触发中文错误的调用）
#    ⚠️ 目录必须放在纯 ASCII 路径下：批处理用 ASCII 写盘，中文路径会变成 "?" 导致找不到文件
$dir = Join-Path $env:TEMP 'mcptest'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$in = Join-Path $dir 'in.ndjson'
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}'
  '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"file_set_public","arguments":{"file_id":336}}}'
) -join "`n" | Set-Content $in -Encoding utf8

# 2) 生成批处理：强制 chcp 936 后运行 MCP，标准流重定向到文件
$dll = 'E:\Code\FileUploadServer\FileUploadServer.Mcp\bin\Release\net10.0\FileUploadServer.Mcp.dll'
$bat = Join-Path $dir 'run.bat'
"@echo off`r`nchcp 936 >nul`r`ndotnet `"$dll`" < `"$in`" > `"$dir\out.json`" 2> `"$dir\err.log`"" |
  Set-Content $bat -Encoding ascii
cmd /c $bat

# 3) 按「原始字节 + UTF-8」解码查看（勿用 PowerShell 管道，会二次转码失真）
[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes("$dir\out.json"))
```

**预期**：修复前含 `ȱ�ٱ������`，修复后为 `缺少必填参数: is_public`。

> **若不设 `chcp 936`，将无法复现** —— 进程在无控制台句柄时 .NET 退回 UTF-8，乱码不出现。这是本次复现走过的弯路，务必保留 chcp。

### 9.5 验证公网链接

```powershell
curl.exe -s -o NUL -w "%{http_code} %{size_download} %{content_type}`n" `
  https://file.sub.opengm.top/p/public/<文件名>
```

### 9.6 运行 MCP 单测

```powershell
dotnet test E:\Code\FileUploadServer\FileUploadServer.Tests\FileUploadServer.Tests.csproj `
  -c Debug --filter "FullyQualifiedName~Mcp"
```

> 用 **Debug** 配置：Release 输出目录可能被运行中的 MCP 进程锁定导致构建失败（`MSB3027/MSB3021`）。

---

## 10. 遗留风险与注意事项

1. **3 个已发布文件是匿名可访问的**：任何拿到 URL 的人都能查看（受 `PublicPathOptions` 的 IP 白名单/黑名单、三层限流、50MB 大小上限约束，见 [06-public-access.md](06-public-access.md) §2）。如需收回，用同一端点传 `isPublic=false`。
2. **MCP 进程会锁定 Release dll**：任何 Release 重建都必须先停止该进程（§5.5），否则报 `MSB3027`。
3. **不要用 `Console.OutputEncoding` 修编码**：stdout 为管道时无控制台句柄，setter 会抛 `IOException`。
4. **测试在 Windows 与 Linux 行为不一致**（§6）：新增用例请勿手拼 JSON，一律用序列化构造路径。
5. **老文件解密问题与本次无关**（密钥丢失导致 `tag mismatch`），见 [06-public-access.md](06-public-access.md) §6。
6. **admin 端点仅本机可达是设计而非故障**：排查 MCP 报错时，先看是哪条路由，再判断是否撞上了这条 ACL。

---

## 11. 关联文档

- [08-mcp.md](08-mcp.md) — MCP 接口实现细节（6 工具）
- [06-public-access.md](06-public-access.md) — `/p/` 公共访问细案、限流、`PUT /api/admin/files/{id}/public` 端点
- [13-mcp-baseline.md](13-mcp-baseline.md) — MCP 通用开发规范（错误码 -32003 定义）
- [12-bug-tracker.md](12-bug-tracker.md) — 踩坑记录（公开访问排查 7 项）
- [14-dev-log.md](14-dev-log.md) — 开发日志
- [11-deployment.md](11-deployment.md) — 部署运维指南（nginx / 网关）
