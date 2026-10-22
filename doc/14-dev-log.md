# 开发日志
> 用途：按日期记录每次开发的完整过程、决策与产出，便于追溯和新人了解项目演进
> 创建：2026-08-02 | 最近更新：2026-10-01 | 关联：[01-architecture.md](01-architecture.md)、[11-deployment.md](11-deployment.md)、[12-bug-tracker.md](12-bug-tracker.md)、[13-mcp-baseline.md](13-mcp-baseline.md)

> **说明**：本文件按日期追加，最新日志在顶部。每次开发结束后在文件尾部新增一节。

---

## 2026-10-01

**记录范围**：Admin Key 重建 + 桌面端 MCP 重新接入 + 文档同步
**提交**：未生成提交（仅本地配置 + 文档变更，见下方）

### 一、事件背景

两个独立但同因触发：
1. **文件管理桌面客户端登不上**：客户端配置中保存的某个 Admin Key（数据库 ID 26/28，描述 `account:admin`）已被软删除（`isDeleted=true`），所有 `?key=...` 调用返回 401。
2. **桌面端 MCP 服务断了**：Claude Code 的 MCP 进程未启动 / 配置丢失，工具列表为空。

### 二、现状盘点（远端 SSH）

```bash
ssh -i ~/.ssh/CouldServer_1.pem ubuntu@111.229.53.125 "curl http://localhost:7000/api/admin/keys"
```

| 密钥 | 类型 | 状态 | 用途 |
|---|---|---|---|
| ID 20 | Admin | ✅ 未删、2216 年过期 | `file-upload-mcp-master`（MCP Master Key） |
| ID 26, 28 | Admin | ❌ 已软删 | `account:admin`（桌面客户端的凭据） |
| ID 30 | User | ✅ 未删 | `qingban-server` |
| 其它 Admin | Admin | 过期/软删 | 历史测试残留 |

**结论**：MCP Master Key 仍有效，问题仅在"客户端持有的 Admin Key"——需要新建并下发。

### 三、关键发现：Bug #8 — `CreateKey` 对 `expireMinutes=0` 创建立即过期

按惯性传 `expireMinutes=0` 表示"永不过期"（历史 ID 20 就是这么建的，过期时间 2216-09-20），结果第一次创建的新密钥（ID 31）`ExpiresAt` 等于 `CreatedAt`（差 1ms），立刻失效。`GET /api/files?key=...` 返回 401。

**根因**：`AdminController.CreateKey`（`AdminController.cs:47-77`）直接 `ExpiresAt = DateTime.UtcNow.AddMinutes(expireMinutes)`，传 0 即为 0。`RenewKey` 同文件 117-121 行已显式拒绝 `<=0`，但 `CreateKey` 漏掉了同样的边界检查。

详见 [12-bug-tracker.md #8](12-bug-tracker.md) — 已记录，建议按 `expireMinutes <= 0 ⇒ AddYears(200)` 修复，与 RenewKey 边界对齐。

**临时方案**：传 `expireMinutes=525600`（1 年）创建长效密钥，符合 Admin 密钥本就不应被自动清理的语义。

### 四、恢复操作

#### 4.1 创建文件管理客户端专用 Admin Key

```bash
ssh -i ~/.ssh/CouldServer_1.pem ubuntu@111.229.53.125 \
  "curl -X POST 'http://localhost:7000/api/admin/keys?description=file-management-client-admin&expireMinutes=525600&keyType=Admin'"
```

**输出**（**密钥值已脱敏**，仅本地 `.mcp.json` 与客户端配置保留）：
```json
{
  "id": 32,
  "key": "<已脱敏，详见本地 .mcp.json>",
  "description": "file-management-client-admin",
  "createdAt": "2026-10-01T11:05:19.8163844Z",
  "expiresAt": "2027-10-01T11:05:19.8163846Z",
  "isDeleted": false,
  "keyType": "Admin"
}
```

验证：`curl 'http://localhost:7000/api/files?key=<新 Admin Key>&pageSize=1'` → `HTTP 200`（本地命令执行历史可查）。

随后清理掉因 `expireMinutes=0` 立即过期的 ID 31 及其它历史过期/已删密钥：`DELETE /api/admin/keys/cleanup` 返回 12（一次性清掉 12 条）。

#### 4.2 桌面端 MCP 重新接入

仓库此前没有 `.mcp.json`（仅凭本机残留配置 + MCP Master Key 工作，机器重装即丢）。本次新增：

- **`.mcp.json`**（**`.gitignore`**）：当前生效的配置，含真实 Master Key
  ```json
  {
    "mcpServers": {
      "file-upload-server": {
        "command": "dotnet",
        "args": ["run", "--project", "E:/Code/FileUploadServer/FileUploadServer.Mcp/FileUploadServer.Mcp.csproj"],
        "env": {
          "FILE_SERVER_BASE_URL": "https://file.sub.opengm.top",
          "FILE_SERVER_MASTER_KEY": "<MCP Master Key，ID 20，长期有效，从本地 .mcp.json 读>"
        }
      }
    }
  }
  ```
- **`.mcp.json.example`**（可入仓）：脱敏模板，`<Admin Key from /api/admin/keys (keyType=Admin)>` 占位
- **`.gitignore`** 增补：`.mcp.json` 一律不追踪

**验证 MCP Server 启动 + 链路**：

```powershell
$env:FILE_SERVER_BASE_URL = "https://file.sub.opengm.top"
$env:FILE_SERVER_MASTER_KEY = "<MCP Master Key，ID 20>"
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}'
  '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"file_list","arguments":{}}}'
) | dotnet run --project FileUploadServer.Mcp/FileUploadServer.Mcp.csproj
```

结果：`initialize` 1ms 成功；`tools/list` 返回 6 个工具；`file_list` 返回完整文件列表（HTTP 200 + JSON 数组）。MCP 链路通畅。

桌面端 Claude Code 只需重启即可读取新 `.mcp.json`，无需手工干预。

### 五、文档同步

- **doc/03-permission.md** — 新增 User KeyType 描述（之前文档只有 Admin/Temporary，与代码 `ApiKeyType.cs` 不一致）
- **doc/12-bug-tracker.md** — 新增 Bug #8（`expireMinutes=0` 边界回归）；新增第 5 章"运维/密钥管理踩坑"4 条；更新教训总结至 9 条
- **doc/14-dev-log.md** — 本节
- **.gitignore** — `.mcp.json` 加入不追踪列表

### 六、后续待办（未提交代码改动）

| # | 事项 | 优先级 |
|---|---|---|
| 1 | 修复 Bug #8：`CreateKey` 对 `expireMinutes<=0` 显式声明"永不过期"语义，与 `RenewKey` 边界对齐 | 中 |
| 2 | 把 `.mcp.json.example` 的占位规则写进 `.claude/skills/file-upload-server-mcp/SKILL.md` | 低 |
| 3 | 文件管理桌面端集成 Admin Key 自动续期接口 `PUT /api/admin/keys/{key}/renew`，避免 1 年后再失效 | 中 |

---

## 2026-08-02

**记录范围**：MCP 接口开发、分布式部署（网关 + WS 节点）、公开访问问题排查与屏蔽、部署流程优化、三入口解密统一、网页删除 WS 清理
**涉及版本**：`b26d430`（MCP + 屏蔽公开访问）、`8faadd5`（统一三入口解密）、`ff83450`（网页删除 WS 清理）、`8758408`（上传本地副本清理）

### 一、概述

本次开发围绕三件事：
1. **为文件服务器实现 MCP 接口**（`FileUploadServer.Mcp`），把上传/下载/删除/列表/详情/公开设置暴露为 AI 可调用的 6 个工具
2. **分布式部署**：网关（Web）更新到最新代码、清理 WS 存储节点重复进程
3. **公开访问问题排查**：定位 `/p/` 路径 503 根因，最终决策**屏蔽公开访问并标注问题待整改**

### 二、MCP 接口开发

#### 2.1 需求与方案

关键决策（与用户确认）：

| 决策点 | 选择 | 理由 |
|---|---|---|
| 协议实现 | **手写 JSON-RPC 2.0 over stdio** | 零第三方依赖、完全掌控自定义错误码（-32602/-32xxx）、与项目手写 WS 协议的风格一致 |
| 鉴权 | 启动时环境变量注入 Master Admin Key | 密钥不进入 LLM 上下文，安全 |
| 超时/重试 | 上传/下载 300s，其他 30s；5xx 指数退避重试 2 次 | 符合规范 |
| 错误码 | 400/404 -> -32602、401/403 -> -32003、429 -> -32004、503 -> -32005、超时 -> -32000、连接失败 -> -32001 | 见 [13-mcp-baseline.md](13-mcp-baseline.md) 错误码规范 |

#### 2.2 实现结构

```
FileUploadServer.Mcp/
├── Program.cs               # stdio 主循环、配置加载、优雅退出
├── McpServerConfig.cs       # 配置（BaseUrl / MasterKey / 超时 / 重试）
├── McpLogger.cs             # 结构化日志（写 stderr，不污染 stdout）
├── Protocol/                # JsonRpcRequest/Response/Error、McpError、StdioTransport
├── Server/                  # McpServer（生命周期状态机）、ToolDefinition（6工具定义）
├── Services/                # McpHttpClient（鉴权/超时/重试）、FileToolHandlers、ErrorMapper
└── Models/                  # FileItemDto、ApiKeyDto
```

**核心设计**：
- **双层错误模型**：参数校验失败 -> JSON-RPC error（-32602）；下游 HTTP 失败 -> CallToolResult `isError:true` + text 含错误码
- **tools/call 永远返回 CallToolResult**（content[].text + isError），业务错误不抛 JSON-RPC error
- **状态机**：`initialize -> notifications/initialized` 门控，未初始化返回 -32002
- **可测试性**：`McpServer.HandleAsync(JsonRpcRequest)` 可被测试直接驱动，不依赖真实 stdio

#### 2.3 6 个工具

| 工具 | HTTP 端点 | 校验 |
|---|---|---|
| `file_list` | GET /api/files | — |
| `file_info` | GET /api/files/{id} | file_id 必填 int |
| `file_upload` | POST /api/files（multipart） | local_file_path 必填、文件存在 |
| `file_download` | GET /api/files/download/{id} | file_id 必填 int，Base64 返回 |
| `file_delete` | DELETE /api/files/{id} | file_id 必填 int |
| `file_set_public` | PUT /api/admin/files/{id}/public | is_public=true 时必须 public_path |

#### 2.4 测试

`FileUploadServer.Tests/Mcp/` 下 **12 个测试文件 + 3 个助手，52 个测试全部通过**，覆盖全部用例（LIFE/LIST/FL/FI/UP/DL/DEL/PUB/AUTH/RETRY/ERR/E2E）。

### 三、部署记录

#### 3.1 网关更新（111.229.53.125:7000）

- 原部署是 7-11 版本（自包含 `./FileUploadServer.Web`），Web 项目有一批未提交改动未上线
- 流程：备份配置 -> 停旧进程 -> 上传 -> 恢复配置 -> 启动 -> 验证
- **关键**：`Storage.Mode: WebSocket` 保持不变，`encryption.key` 绝不能覆盖

#### 3.2 WS 存储节点清理（192.168.1.4）

- 发现 **3 个同 client-id 的重复 WsClient 进程**（7-11 当天先后启动）-> 全部清理为单实例
- 发现 **WS 认证长期失败**：旧 client-secret 的 SHA256 与数据库 `ClientSecretHash` 不匹配 -> 通过 `regenerate-secret` API 生成新密钥
- 单实例 + 正确密钥后稳定连接（`Connected successfully`）

#### 3.3 MCP Server 接入

- MCP Server 走 stdio，**运行在 Claude Code 本机**，不部署到远程
- 配置 `.mcp.json`：`FILE_SERVER_BASE_URL=http://111.229.53.125:7000` + Admin 密钥

### 四、踩坑汇总

详见 [12-bug-tracker.md](12-bug-tracker.md)。本次开发/部署/排查中遇到的主要问题：

- **MCP 开发**：静态初始化顺序、JsonNode API 差异、.NET 10 multipart 格式变化等 7 项
- **部署**：pkill 误杀 SSH、text file busy、nohup 挂起、WS secret 不匹配等 7 项
- **公开访问排查**：`StartsWithSegments("/p/")` 不匹配 bug、老文件密文不兼容等 3 项

### 五、总结归纳

#### 5.1 架构认知修正

- 本项目的"前后端分离"是**网关（Web）+ WS 存储节点（WsClient）**的分布式架构，不是传统 Web 前后端
- **访问路径只有两条**：API 请求（`FileApiController`）和公开访问（`/p/`，本应只读本地文件）
- `PublicFileMiddleware` 的 WS 直接读取（Step 8.5）是**未提交的新增改动**，违背"访问统一走 API"的分层架构，且对加密文件解密失败——**本次已屏蔽**

#### 5.2 设计原则沉淀

1. **分层**：中间件不绕过 API 层直接操作存储策略
2. **不改好的部分**：API 下载返回密文（前端解密）是既有设计，未被破坏
3. **数据 vs 代码问题**：老密文无法解密是数据问题，改代码无效，需重传
4. **部署标准化**：部署前 git 提交推送 + 敏感信息检查 + 部署后清理（已写入 deploy skill）

### 六、后续计划

#### 6.1 公开访问整改（高优先）

1. **统一封装**：公开访问走 `FileApiController.Download` 的共享封装（或公开文件限定本地磁盘）
2. **修复 `ApiKeyAuthMiddleware:27` bug**：`StartsWithSegments("/p/")` -> `StartsWithSegments("/p")` 或 `Path.StartsWith("/p/")`
3. **重新启用 `PublicFileMiddleware`**（修复后），删除其 Step 8.5 或改为走 API 封装

#### 6.2 老文件恢复

- p.txt/d.txt/fresh.txt/Markdown入门.md 等老文件密文无法解密 -> 如需恢复，由用户提供原文件重新上传

#### 6.3 MCP 增强

- 实现 `resources/read`（暴露文件元数据为资源）
- 可选：编写 OpenAPI -> tools/list 的自动生成脚本

#### 6.4 运维

- 网关配置备份（.bak）确认稳定后清理
- 定期检查 WS 节点单实例状态（多进程会导致断连）
- 部署 skill 随实践持续完善

### 七、本次关键产物

- `FileUploadServer.Mcp/` — MCP Server（手写 JSON-RPC 2.0 over stdio）
- `FileUploadServer.Tests/Mcp/` — 52 个单元测试
- `.claude/skills/deploy-file-upload-server/SKILL.md` — 优化后的部署 skill（git 提交推送 + 敏感信息检查 + 部署后清理）
- `.claude/skills/file-upload-server-mcp/SKILL.md` — MCP 接入 skill
- `doc/` 下全套新文档体系

### 八、修复三个下载入口解密不一致 + 网页删除 WS 清理

**涉及版本**：`8faadd5`（fix: 统一三个下载入口解密逻辑）、`ff83450`（fix: 网页删除补上 WS 节点文件/FileLocation/加密子目录清理）

#### 8.1 三入口解密不一致修复

**问题**：网页 / MCP / 公共访问三个下载入口对 WS 加密文件解密行为不一致：
- 网页下载（`Download.cshtml.cs` Razor Page）**独立解密** → 正常
- MCP 下载（`FileApiController.Download` 的 WS 分支）**漏解密**，直接返回 `FUEC` 密文
- 公共访问（`PublicFileMiddleware`）独立中间件，本地分支无解密（TODO），WS 分支解密异常被吞

**根因**：解密逻辑未统一到一处，三处各自为政，只有网页那条碰巧正确。

**修复**：
- 新建 `Web/Services/FileDownloadService.cs`：统一「读取（WS/本地）+ 透明解密」，三入口共用
- `FileApiController.Download` 补上 WS 分支解密（MCP 恢复明文）
- `Download.cshtml.cs` 改用共享服务，顺带修复加密文件本地路径（子目录 + DiskFileName）404 bug
- `PublicFileMiddleware` 删除 WS 直连分支、统一走共享服务
- `ApiKeyAuthMiddleware` 修复 `/p/` 跳过 bug（`StartsWithSegments("/p/")` → `"/p"`）
- **重新启用 `/p/` 公共访问**（Program.cs 取消屏蔽）

**验证**：新上传文件三入口返回相同明文；公共访问 `/p/public/hello.txt` 明文返回。

#### 8.2 网页删除不清理 WS 节点文件修复

**问题**：`Index.cshtml.cs` 网页删除只删本地 `uploads/StoredFileName`，不删 WS 节点远程文件、不删加密子目录文件、不删 FileLocation 记录 → WS 节点密文永久残留。

**修复**：
- 新建 `Web/Services/FileDeleteService.cs`：统一清理 WS 远程文件 + FileLocation + 本地物理文件（加密子目录）
- `Index.cshtml.cs` 两个删除方法、`FileApiController.Delete` 统一调用

**验证**：网页删除后 WS 节点文件、FileLocation、数据库记录全部清理。

#### 8.3 遗留问题

- 历史文件（44/46/47-54 共 10 个）用已丢失密钥加密，当前密钥无法解密，需重新上传
- 网页删除需 antiforgery token（ASP.NET Razor Pages 默认），自动化测试需先获取 token 再 POST

### 九、上传本地副本残留根治 + 历史垃圾三层对齐

**涉及版本**：`8758408`（fix: 上传 WS 转发成功后删除网关本地临时副本）

#### 9.1 问题

上传流程先在网关本地 `wwwroot/uploads` 加密写一份临时副本，再转发 WS 节点，但本地副本从不删除。导致网关本地累积 **16 个无记录对应的孤儿密文**（即使文件已删除也残留，不影响功能但持续膨胀）。

#### 9.2 修复

- `FileApiController.Upload` / `Index.cshtml.cs` OnPostAsync：**WS 转发成功后删除本地临时副本**（本地仅作中转，正式存储为 WS 节点）；WS 转发失败降级本地时保留本地文件
- 手动清理历史垃圾，使三层完全对齐：
  - 网关本地 16 个孤儿密文
  - 数据库 3 条孤儿 FileLocation 记录（/t.txt、/public/upload/final_test.txt）
  - WS 节点历史垃圾文件（/ccc/ttt、/texture/image*.png 等 10 个历史文件的密文）
- 清理后数据库 / FileLocation / WS 节点 / 网关本地仅剩 3 个可解密文件（40/41/42），完全一致

#### 9.3 运维补充

WS 节点（192.168.1.4）经 `~/.ssh/id_rsa_self` **公钥免密**访问（密码认证实际不可用）。详见 [11-deployment.md](11-deployment.md)。

### 十、WS 客户端二进制帧跨缓冲区截断修复

**涉及版本**：`2383d26`（fix: WS 客户端二进制帧跨缓冲区截断丢失尾部数据）

#### 10.1 问题

把 Doc 目录 `Mysql接入记录.md` 的 8 张截图上传为公开图片时，`image-5.png`（明文 95751 字节）公开访问返回 401。排查链路：网关日志 `DiskSize=95827`（加密正确），但 WS 节点实际密文 **95803**，少 24 字节 → 解密 `tag mismatch` → 异常落入 ExceptionHandler → 重放 `/Error` 无 key → 401。

规律：密文 < 65536 的 7 张图片全部正常，唯独密文 95827 > 65536 需要**分块**的 image-5.png 丢失 24 字节。curl 直传网关复现，排除 MCP 链路。

#### 10.2 根因

`WsFileStorageClient.ReceiveLoopAsync` 接收缓冲区为 `1024*64=65536` 字节，而二进制帧大小 = 帧头(24B: requestId16+chunkIndex4+totalChunks4) + 数据(64KB) = **65560B，超过缓冲区**。Binary 分支未像 Text 分支那样累积到 `EndOfMessage` 再处理，导致跨缓冲区边界的帧被截断，尾部 24 字节（恰为帧头长度）丢失。

#### 10.3 修复

Binary 分支与 Text 分支一致：写入 `messageBuffer`，`EndOfMessage` 后取完整帧交给 `HandleBinaryMessage(byte[] frame)` 解析。重新部署 WS 节点（192.168.1.4）后重传 image-5.png，密文恢复 95827、公开访问 200 正常。

#### 10.4 教训

WebSocket 接收循环必须处理分片：当接收缓冲区小于单帧大小时 `ReceiveAsync` 只返回部分数据（`EndOfMessage=false`），必须累积到消息完整再解析，Text / Binary 两种消息类型处理要一致。

### 十一、公开访问与文档对齐（Bug：公开访问与文档不符）

**问题**：提交 `8faadd5` 重新启用 `/p/` 公开访问后，多份文档与代码仍停留在「已屏蔽」状态：
- `00-overview` / `01-architecture` / `02-api-reference` / `03-permission` / `04-encryption` / `09-rate-limit` / `11-deployment` 多处描述「/p/ 已屏蔽」
- `PublicFileMiddleware.cs` 顶部 `<remarks>` 注释仍写「⛔ 已由 Program.cs 屏蔽（未注册）」
- `06-public-access` 第 2-6 节残留「原始 12 步流程」「WS 直连分支不可达」「返回密文」等过时描述

**修复**：
1. `PublicFileMiddleware.cs` 顶部注释改为「✅ 已重新启用」，说明统一走 `FileDownloadService` 读取 + 解密
2. 重写 `doc/06-public-access.md` 至当前实现（处理流程、统一读取 + 解密、遗留问题、端点清单）
3. 同步 7 份文档「已屏蔽」→「已启用」，并更正 `ApiKeyAuthMiddleware` `/p/` 跳过 bug 已修复、ETag 前缀长度 `[0..16]` 描述

**验证**：全局 grep 复查，仅历史日志 / MCP 错误码保留「屏蔽/不可达」字样，无现状描述残留。

### 十二、.NET 10.0.13 运行时安全升级（网关 + 认证服务）

**日期**：2026-10-22 | **提交**：`890a234`（chore(security): 固定自包含运行时 10.0.13 并对齐 EF Core/Npgsql 补丁版本）

#### 12.1 背景

服务器 4 个 .NET 服务中，网关（7000）与认证服务（7100）是 **self-contained** 发布，各自内置 .NET 10.0.9 运行时。
宿主机 `/usr/share/dotnet` 升级**不会**修复它们——不重新发布就等于带着旧运行时继续跑在公网。
参照 [WebDoor 评估报告](https://github.com/) 与微软 2026-10-13 维护公告，修复版本为 **10.0.13**（含 ASP.NET Core）。

#### 12.2 变更

| 项 | 变更前 | 变更后 |
|----|--------|--------|
| 网关运行时 | 10.0.9（内置） | **10.0.13**（内置） |
| 认证服务运行时 | 10.0.9（内置） | **10.0.13**（内置） |
| SDK | 10.0.400-preview（不可用于出补丁版本） | **10.0.402**（内置运行时 10.0.13） |
| Microsoft.EntityFrameworkCore(.Design/.Sqlite/.Tools) | 10.0.5 | 10.0.13 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.1 | 10.0.2 |

代码侧：`FileUploadServer.Web.csproj` 与 `FileUploadServer.Auth.csproj` 写死
`<RuntimeFrameworkVersion>10.0.13</RuntimeFrameworkVersion>`，并显式声明
`<ServerGarbageCollection>true</ServerGarbageCollection>`（与线上 runtimeconfig 的 `System.GC.Server` 保持一致）。

#### 12.3 部署方式改为「目录整体切换」

原流程是覆盖式上传，导致线上目录堆积了历史残留：Tests 程序集、coverlet、`createdump.exe`、win-x64 的 `.exe`、
多 RID 目录（alpine/arm64/macos/x86）。本次改为：

**停服 → 旧目录改名备份 → 解压新包 → 恢复生产配置与数据 → 权限 → 启动 → 验证**

要点：
- 打包时排除 `appsettings*.json`，生产配置与 `encryption.key` 只在远端，从备份目录 `cp -p` 恢复
- `wwwroot/uploads`（本地存储的历史文件）随目录一并带过去
- Windows 打的 tar 不带可执行位，远端必须 `chmod 755` 可执行文件、`chmod 600 encryption.key`
- 回滚 = 把 `.bak-<TS>` 目录换回并重启，秒级完成

#### 12.4 踩坑

1. **跨平台发布必须指定 RID**：`--self-contained true` 不带 `-r linux-x64` 时会按开发机 RID（win-x64）产出，产物在 Linux 上起不来
2. **预览版 SDK 不能出安全补丁**：`10.0.400-preview` 内置的不是 10.0.13，必须另装稳定 SDK `10.0.402`
3. **`pgrep -f` 会匹配到 SSH 命令行自身**：停服务改用 `ss -tlnp` 按端口取 PID（见 [11-deployment.md](11-deployment.md) §8.1）
4. **从 PowerShell 管道喂 bash 脚本会带 BOM 和 CRLF**：`bash` 报 `set: command not found` 或文件名带 `\r`。
   远程脚本用 base64 传输：`echo '<base64>' | base64 -d | bash`
5. **`set -euo pipefail` + `| head` 会因 SIGPIPE（exit 141）中断脚本**，校验脚本里不要把 `head` 和 `set -e` 混用

#### 12.5 验证结果

| 检查项 | 结果 |
|--------|------|
| runtimeconfig `includedFrameworks` | 10.0.13（两个服务） |
| `libcoreclr.so` 版本串 | 10.0.13（两个服务） |
| 进程 / 端口 | 7000、7100 均在听 |
| 接口 | 网关 `/` → 401、`/swagger` → 200；认证 `/api/auth/me` → 401 |
| WS 存储节点 | 网关重启后自动重连，`isOnline=true` |
| 端到端 | MCP 上传 → WS 节点存储 → 下载，内容一致（随后已删除测试文件） |
| 公网 | file.sub → 401 / Swagger 200；filelogin.sub → 404（无根路由）/ me 401 |

#### 12.6 遗留建议（未实施，属功能改动）

- `ApiKeyAuthMiddleware` 在校验 key **之前**读取 `Request.Form`，multipart 会在鉴权前被解析；
  建议把凭据改到请求头/查询参数，并在解析前限制请求体大小
- 7000 端口目前可绕过 Nginx 直连公网，建议收紧
- `FileUploadServer.Infrastructure` 仍引用 `Microsoft.EntityFrameworkCore.Sqlite`，与「只用 PostgreSQL」的项目规则不符，可评估移除

### 十三、鉴权加固（CVE-2026-70181 暴露面收敛）+ 对外暴露面收敛

**日期**：2026-10-22 | **提交**：`1b3dff5`（fix(security): 凭据改用请求头，鉴权前不再解析请求体）

#### 13.1 问题

`ApiKeyAuthMiddleware` 在校验密钥**之前**读取 `Request.Form` 取 key：

```csharp
if (string.IsNullOrEmpty(key) && context.Request.HasFormContentType)
{
    key = context.Request.Form["key"].FirstOrDefault();   // ← 触发 multipart 解析
}
```

`Request.Form` 会解析整个 multipart 请求体。也就是说：**未携带任何凭据的请求也能让服务端解析并缓冲上传体**，
配合 1GB 的 multipart 上限与 10MB 内存缓冲阈值，构成 CVE-2026-70181（multipart 缓冲导致内存耗尽）的暴露面。
同一批问题还有：凭据走查询串（落入 Nginx 访问日志 / 浏览器历史 / Referer）、7000 端口可绕过 Nginx 直连公网。

#### 13.2 修复

| 项 | 变更 |
|----|------|
| 凭据来源 | 优先 `X-API-Key` 请求头；`?key=` 查询参数仅作兼容保留（浏览器 `<a href>` 下载链接无法带请求头） |
| 表单取 key | **删除**——鉴权阶段完全不碰请求体 |
| 请求体闸门 | 鉴权前按 `Content-Length` 判定，超 `Upload:MaxRequestBodySize`（默认 1GB）直接 413 |
| multipart 缓冲 | `MemoryBufferThreshold` 10MB → **1MB**（超过即落盘）；`ValueLengthLimit` 由 `int.MaxValue` → 64KB |
| MCP 客户端 | 改为 `X-API-Key` 请求头，不再拼 `?key=` |
| 网页端 | 上传 / 删除 / 批量删除表单的 key 改走查询串，表单体不再携带凭据 |
| `/api/file-public` | 支持 `X-API-Key` 请求头（原本只有 `?key=`） |

#### 13.3 对外暴露面收敛

| 组件 | 变更前 | 变更后 |
|------|--------|--------|
| 网关监听 | `0.0.0.0:7000`（公网可直连） | `127.0.0.1:7000`（公网不可达） |
| Nginx | `proxy_pass http://localhost:7000` | `proxy_pass http://127.0.0.1:7000` |
| WS 存储节点 | `--server ws://111.229.53.125:7000` | `--server wss://file.sub.opengm.top`（经 Nginx WebSocket 升级） |

> **顺序**：先切 WS 节点到 wss 并确认在线，再改绑网关。反过来做会切断 WS 节点唯一的连接通道，存储立即不可用。

#### 13.4 踩坑

1. **网页端表单不能直接把 key 放请求头**：浏览器原生表单无法自定义请求头。改为把 key 放进 form 的 action 查询串
   （`asp-route-key`），Razor Pages 的 `key` 属性照常从查询串绑定。
2. **批量删除是 JS 构造的表单**：`form.action` 需手工拼 `&key=...`，隐藏字段已移除。
3. **测试工程引用 Web 工程**：需给测试工程加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`。
4. **模拟"不读表单"要用特性替身**：`IFormFeature` 的 `Form` getter 直接抛异常；注入要用 `context.Features.Set<IFormFeature>(...)`，
   因为 `context.Request.FormFeature = ...` 的赋值实现会先读一次 `value.Form`。

#### 13.5 验证

| 检查项 | 结果 |
|--------|------|
| `X-API-Key` 请求头鉴权 | 200 |
| `?key=` 查询串兼容 | 200 |
| 无凭据 | 401 |
| multipart 无凭据 | 401（请求体未被解析） |
| multipart + 请求头凭据 | 201 |
| 公网直连 7000 | 000/503（不可达） |
| `https://file.sub.opengm.top/` | 401；`/swagger` 200；`/api/public/files` 200 |
| WS 节点 | 经 wss 在线（`isOnline=true`），网关日志中连接 Host 为 `file.sub.opengm.top` |
| 端到端 | MCP 上传 → wss → WS 节点存储 → 下载，内容一致（测试文件已删除） |
| 单元测试 | 59 通过（含新增 7 项中间件测试） |

#### 13.6 遗留

- 认证服务 7100 仍未绑回环（当前靠云安全组拦截），可同样改为 `127.0.0.1:7100`
- 网页下载链接仍把 key 放在查询串（浏览器限制），彻底解决需改为短期下载令牌

#### 13.7 收尾（同日追加）

- **WS 存储节点（192.168.1.4）重建到 10.0.13**：此前仍是 10.0.6。`FileUploadServer.WsClient.csproj` 补上
  `RuntimeFrameworkVersion=10.0.13`，用 SDK 10.0.402 发布 linux-x64 自包含包，走「目录整体切换」上线：
  新包解压 `wsclient.new` → 交换目录 → 停旧进程 → `start-wsclient.sh` 启动（脚本内 `pgrep` 守卫防双实例）。
  节点仍是单实例、`libcoreclr.so` 版本串 10.0.13、`/home/laowang/wsdata` 数据完好（597MB），端到端上传下载一致。
- **认证服务改绑 `127.0.0.1:7100`**：Nginx `filelogin` 的 `proxy_pass` 本就是 `http://127.0.0.1:7100`，
  改绑不影响公网入口；本机 `localhost:7100` 接口与错误凭据登录均正常返回 401。
- 至此服务器上 **7000 / 7100 都只监听回环**，公网入口仅剩 443（Nginx），云安全组可关闭 7000 / 7100 入站规则。
- WS 节点上的 `client-secret` 明文按"内网机没必要"的约定保持现状。

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览
- [02-api-reference.md](02-api-reference.md) — HTTP API 完整参考
- [11-deployment.md](11-deployment.md) — 部署运维指南
- [12-bug-tracker.md](12-bug-tracker.md) — 踩坑记录（本文档踩坑汇总的详细版）
- [13-mcp-baseline.md](13-mcp-baseline.md) — MCP 通用开发规范模板
