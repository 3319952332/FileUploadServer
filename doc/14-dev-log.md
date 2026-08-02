# 开发日志
> 用途：按日期记录每次开发的完整过程、决策与产出，便于追溯和新人了解项目演进
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md)、[11-deployment.md](11-deployment.md)、[12-bug-tracker.md](12-bug-tracker.md)、[13-mcp-baseline.md](13-mcp-baseline.md)

> **说明**：本文件按日期追加，最新日志在顶部。每次开发结束后在文件尾部新增一节。

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

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览
- [02-api-reference.md](02-api-reference.md) — HTTP API 完整参考
- [11-deployment.md](11-deployment.md) — 部署运维指南
- [12-bug-tracker.md](12-bug-tracker.md) — 踩坑记录（本文档踩坑汇总的详细版）
- [13-mcp-baseline.md](13-mcp-baseline.md) — MCP 通用开发规范模板
