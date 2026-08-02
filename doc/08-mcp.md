# MCP 接口细案

> 用途：完整说明 FileUploadServer.Mcp 的架构设计——JSON-RPC 2.0 协议、6 个工具定义、服务器生命周期、HTTP 客户端与错误映射，作为 [01-architecture.md](01-architecture.md) 中 MCP 组件的深化补充。
> 创建：2026-08-02 | 关联：[01-architecture.md](01-architecture.md) / [02-api-reference.md](02-api-reference.md) / [07-ws-storage.md](07-ws-storage.md)

## 目录

1. [架构总览](#架构总览)
2. [McpServer 生命周期与状态机](#mcpserver-生命周期与状态机)
3. [JSON-RPC 方法分发](#json-rpc-方法分发)
4. [6 个工具定义](#6-个工具定义)
5. [FileToolHandlers 处理器](#filetoolhandlers-处理器)
6. [McpHttpClient HTTP 客户端](#mcphttpclient-http-客户端)
7. [ErrorMapper 错误码映射](#errormapper-错误码映射)
8. [配置体系](#配置体系)
9. [StdioTransport](#stdiotransport)
10. [测试覆盖](#测试覆盖)
11. [关键类/文件](#关键类文件)
12. [关联文档](#关联文档)

---

## 架构总览

```
┌────────────────~~~~~~~~~~~~~~~~──┐
│          Claude Code / AI 代理    │
│              (stdio)              │
└────────────┬─────────────────────┘
             │ stdin/stdout (NDJSON)
             ▼
┌────────────────~~~~~~~~~~~~~~~~──┐
│    FileUploadServer.Mcp           │
│    ┌───────────────────────────┐  │
│    │  StdioTransport            │  │  read stdin / write stdout
│    │  McpServer                 │  │  状态机 + 方法分发
│    │  ToolDefinitions (6个)     │  │  工具元数据
│    │  FileToolHandlers          │  │  参数校验 + HTTP 调用
│    │  McpHttpClient             │  │  超时 + 重试 + key 注入
│    │  ErrorMapper               │  │  HTTP 状态 → MCP 错误码
│    └───────────────────────────┘  │
└────────────┬─────────────────────┘
             │ HTTP + ?key=MasterApiKey
             ▼
┌──────────────────────────────────┐
│  FileUploadServer.Web (网关)     │
│  /api/files/...                 │
└──────────────────────────────────┘
```

核心特点：
- **本地运行**：MCP Server 与 AI 客户端在同一主机，通过 stdio 通信
- **远程调用**：通过 HTTP 调用远程网关，URL 自动附加 `?key=MasterApiKey` 鉴权
- **协议**：JSON-RPC 2.0（每行一条完整的 JSON，NDJSON 格式）

---

## McpServer 生命周期与状态机

来源：`FileUploadServer.Mcp/Server/McpServer.cs`。

### 状态机

```
  启动    initialize    initialized        shutdown      退出
  ──►  ──►  ──►  ──►  ──►  ──►  ──►  ──►  ──►  ──►  ──►
       (收到protocolVersion)  (设置_initialized=1)   (ShutdownRequested=true)
```

### 生命周期核心属性

| 属性 | 类型 | 说明 |
|---|---|---|
| `IsInitialized` | `bool` | 收到 `notifications/initialized` 后设为 true；未初始化时调用 `tools/list` 或 `tools/call` 返回 -32002 |
| `ShutdownRequested` | `bool` | 收到 `shutdown` 方法或 `exit` 通知后设为 true，`Program.Main` 据此退出主循环 |
| `ServerName` | `const` | `"file-upload-server-mcp"` |
| `ServerVersion` | `const` | `"1.0.0"` |

### 支持的协议版本

```csharp
"0.1.0", "1.0",
"2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-03-26"
```

`initialize` 时客户端声明的 `protocolVersion` 必须在此集合中，否则返回 -32602。

---

## JSON-RPC 方法分发

`HandleAsync(JsonRpcRequest)` 核心分发逻辑：

| 方法 | 类型 | 行为 | 备注 |
|---|---|---|---|
| `initialize` | 请求 | 校验 `protocolVersion` → 返回 `serverInfo` + `capabilities` | 必须最先调用 |
| `ping` | 请求 | 返回空 `{}` | 标准 MCP 保活 |
| `tools/list` | 请求 | 返回 6 个工具的 JSON Schema 列表 | 需要 `IsInitialized=true` |
| `tools/call` | 请求 | 解析 `name` + `arguments` → `_handlers.InvokeAsync()` | 需要 `IsInitialized=true` |
| `shutdown` | 请求 | `ShutdownRequested=true` → 返回空 `{}` | 服务端收到后退出 |
| `resources/list` | 请求 | 返回 `{"resources":[]}` | 预留，当前空列表 |
| `notifications/initialized` | 通知 | `_initialized=1` | 标记初始化完成 |
| `exit` | 通知 | `ShutdownRequested=true` | 客户端退出 |
| `notifications/cancelled` | 通知 | 忽略 | 预留 |
| `logging/setLevel` | 通知 | 忽略 | 预留 |

### 错误码

| 错误码 | 名称 | 来源 | 说明 |
|---|---|---|---|
| -32600 | InvalidRequest | JSON-RPC 标准 | 无效请求 |
| -32601 | MethodNotFound | JSON-RPC 标准 | 未知方法 |
| -32602 | InvalidParams | JSON-RPC 标准 + McpError | 参数非法 / 文件不存在 / 文件过大 |
| -32603 | InternalError | JSON-RPC 标准 + ErrorMapper | 服务器内部错误 (500+) |
| -32002 | NotInitializedError | MCP 自定义 | 未初始化即调用 tools 方法 |
| -32003 | PermissionDenied | MCP 自定义 + ErrorMapper | 401/403 鉴权失败 |
| -32004 | RateLimited | MCP 自定义 + ErrorMapper | 429 触发限流 |
| -32005 | StorageUnavailable | MCP 自定义 + ErrorMapper | 503 存储节点不可用 |
| -32006 | Timeout | MCP 自定义 + McpHttpClient | 请求超时 |
| -32007 | ServiceUnreachable | MCP 自定义 + McpHttpClient | 服务不可达 |

---

## 6 个工具定义

来源：`FileUploadServer.Mcp/Server/ToolDefinition.cs`（`ToolDefinitions` 静态类）。

每个工具包含 `Name`、`Description`（含使用场景与注意事项——这是 LLM 决策的唯一依据）和 `InputSchema`（JSON Schema 7）。

| 工具名 | 对应 HTTP 端点 | 参数 | 说明 |
|---|---|---|---|
| `file_list` | `GET /api/files` | 无 | 获取当前 API Key 可访问的文件列表 |
| `file_info` | `GET /api/files/{id}` | `file_id` (int, 必填) | 获取单个文件元数据 |
| `file_upload` | `POST /api/files` (multipart) | `local_file_path` (string, 必填), `remote_path` (string, 可选) | 上传文件 |
| `file_download` | `GET /api/files/download/{id}` | `file_id` (int, 必填) | 下载文件，Base64 编码返回 |
| `file_delete` | `DELETE /api/files/{id}` | `file_id` (int, 必填) | 删除文件（不可逆） |
| `file_set_public` | `PUT /api/admin/files/{id}/public` | `file_id` (int, 必填), `is_public` (bool, 必填), `public_path` (string, is_public=true 时必填) | 设置公共访问标记 |

### 工具定义详细描述摘录

`file_upload` 描述包含：
- 支持本地磁盘和 WS 远程存储（根据路径前缀自动路由）
- 最大 1GB 单文件
- `remote_path` 必须以 `/` 开头
- 上传成功后返回 `file_id` 供后续操作

`file_download` 描述包含：
- 透明解密（加密文件）
- 大文件最多 300s 超时
- Base64 编码返回
- WS 离线时返回 -32005

`file_set_public` 描述包含：
- 需要 Admin 密钥
- 公开文件受 IP 白名单、限流等多重保护

---

## FileToolHandlers 处理器

来源：`FileUploadServer.Mcp/Services/FileToolHandlers.cs`。

### InvokeAsync 入口

```csharp
public async Task<CallToolResult> InvokeAsync(string toolName, JsonObject? arguments)
{
    return toolName switch
    {
        "file_list" => await HandleFileListAsync(),
        "file_info" => await HandleFileInfoAsync(arguments),
        "file_upload" => await HandleFileUploadAsync(arguments),
        "file_download" => await HandleFileDownloadAsync(arguments),
        "file_delete" => await HandleFileDeleteAsync(arguments),
        "file_set_public" => await HandleFileSetPublicAsync(arguments),
        _ => throw new McpError(-32601, $"Unknown tool: {toolName}"),
    };
}
```

### 参数校验

通过 `RequireInt`、`RequireBool`、`RequireString` 三个辅助方法进行：
- 缺失必填参数 → `McpError(-32602, "缺少必填参数: {name}")`
- 类型不匹配 → `McpError(-32602, "参数类型错误: {name} 必须是{type}")`

`file_upload` 额外校验：本地文件路径不存在 → `McpError(-32602, "文件不存在: {path}")`。
`file_set_public` 额外校验：`is_public=true` 但 `public_path` 为空 → `McpError(-32602, "is_public=true 时必须提供 public_path")`。

### 各 Handler 特点

| Handler | HTTP 方法 | 重试? | 超时 | 特殊处理 |
|---|---|---|---|---|
| `HandleFileListAsync` | GET | 是 | 30s | — |
| `HandleFileInfoAsync` | GET | 是 | 30s | — |
| `HandleFileUploadAsync` | POST multipart | **否** (`SendOnceAsync`) | 300s | MIME 类型自动推断 |
| `HandleFileDownloadAsync` | GET | 是 (不重试超时) | 300s | Base64 编码返回 |
| `HandleFileDeleteAsync` | DELETE | 是 | 30s | — |
| `HandleFileSetPublicAsync` | PUT | 是 | 30s | JSON body: `{isPublic, publicPath}` |

### MIME 类型推断

`GetMimeType(path)` 根据扩展名返回 22 种常见 MIME 类型（含 Office 文档），无匹配则返回 `application/octet-stream`。

---

## McpHttpClient HTTP 客户端

来源：`FileUploadServer.Mcp/Services/McpHttpClient.cs`。

### URL 构建

```csharp
// BuildUrl(endpoint):
$"{FileServerBaseUrl}{endpoint}?key={Uri.EscapeDataString(MasterApiKey)}"
// 如果 endpoint 已含 ?，则用 & 连接
```

### 超时控制

| 场景 | 值 | 配置项 |
|---|---|---|
| 大文件传输（上传/下载） | 300s | `RequestTimeoutSeconds` |
| 其他操作（列表/详情/删除） | 30s | `ShortRequestTimeoutSeconds` |

使用 `CancellationTokenSource.CancelAfter(timeout)` 精确控制每次请求的超时。

### SendWithRetryAsync — 指数退避重试

```csharp
// 重试条件：仅 5xx 状态码、超时、连接失败
// 不重试：4xx 状态码、取消令牌触发
for (attempt = 0; attempt < MaxRetries + 1; attempt++)
{
    → SendAsync (with CancellationTokenSource)
    → 5xx 且非最后一次 → DelayBeforeRetryAsync
    → TaskCanceledException (timeout) → retryOnTimeout? 重试 : 抛 Timeout
    → HttpRequestException → 非最后一次? 重试 : 抛 ServiceUnreachable
}
```

退避延迟公式：

```
delay = retryBaseDelay (默认 1s) * 3^attempt
```

即：1s → 3s → 9s（attempt 0, 1, 2）。

### SendOnceAsync — 不重试请求

用于文件上传等不可重复发送的请求（multipart 含流，重复发送会致后端重复存储）。失败直接抛 McpError。

---

## ErrorMapper 错误码映射

来源：`FileUploadServer.Mcp/Services/ErrorMapper.cs`。

### HTTP 状态 → MCP 错误码映射

| HTTP 状态码 | MCP 错误码 | 错误码值 | 语义 |
|---|---|---|---|
| 400 | InvalidParams | -32602 | 参数非法 |
| 404 | InvalidParams | -32602 | 文件不存在 |
| 413 | InvalidParams | -32602 | 文件过大：超出大小限制 |
| 401 | PermissionDenied | -32003 | 密钥无效或已过期 |
| 403 | PermissionDenied | -32003 | 权限不足 |
| 429 | RateLimited | -32004 | 触发限流 |
| 503 | StorageUnavailable | -32005 | 存储节点不可用 |
| 500+ / 其他 | InternalError | -32603 | 服务器内部错误 |

### 错误响应格式（tools/call 的 isError:true）

```json
{
  "status": "error",
  "error_code": -32602,
  "message": "文件不存在：File with id 999 not found",
  "data": {
    "http_status": 404,
    "retryable": false,
    "context": "file_info id=999"
  }
}
```

- 429 时附加 `retry_after_seconds`（取自 `Retry-After` header，无 header 时默认 30s）
- 消息优先透传后端返回的业务语义（如 "Storage client is currently offline"）

---

## 配置体系

来源：`FileUploadServer.Mcp/McpServerConfig.cs`。

### 配置来源优先级

1. `appsettings.json` 的 `McpServer` 节
2. 环境变量 `FILE_SERVER_BASE_URL`、`FILE_SERVER_MASTER_KEY`（覆盖 appsettings.json）

### 配置项

| 配置项 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `FileServerBaseUrl` | string | `http://localhost:5000` | 后端网关地址 |
| `MasterApiKey` | string | `""` | Admin 类型 Master API Key（**必须配置**） |
| `RequestTimeoutSeconds` | int | 300 | 上传/下载超时（秒） |
| `ShortRequestTimeoutSeconds` | int | 30 | 轻量操作超时（秒） |
| `MaxRetries` | int | 2 | 最大重试次数 |

### 配置示例

```json
{
  "McpServer": {
    "FileServerBaseUrl": "http://111.229.53.125:7000",
    "MasterApiKey": "",
    "RequestTimeoutSeconds": 300,
    "MaxRetries": 2
  }
}
```

### 启动校验

`MasterApiKey` 为空时 `Validate()` 抛出 `InvalidOperationException`，Program 打印错误并返回 exit code 1。

---

## StdioTransport

来源：`FileUploadServer.Mcp/Protocol/StdioTransport.cs`。

- 从 `Console.In` 逐行读取（NDJSON），读到 `null` 表示 EOF（客户端退出）
- `WriteResponse(json)` → 写入 `Console.Out` 并 `Flush()`
- 主循环：`transport.ReadMessageAsync()` → `JsonRpcRequest.TryParse(line)` → `server.HandleAsync(request)` → `transport.WriteResponse(response.ToJsonText())`

---

## 测试覆盖

测试位于 `FileUploadServer.Tests/Mcp/`，共 12 个测试文件：

| 测试文件 | 覆盖内容 |
|---|---|
| `McpLifecycleTests.cs` | 生命周期：initialize → initialized → shutdown |
| `McpToolsListTests.cs` | tools/list 返回、未初始化拒绝 |
| `McpFileListTests.cs` | file_list handler |
| `McpFileInfoTests.cs` | file_info handler |
| `McpFileUploadTests.cs` | file_upload handler（含 SendOnceAsync 不重试验证） |
| `McpFileDownloadTests.cs` | file_download handler（含 Base64 编码验证） |
| `McpFileDeleteTests.cs` | file_delete handler |
| `McpFileSetPublicTests.cs` | file_set_public handler |
| `McpErrorMappingTests.cs` | ErrorMapper 所有 HTTP 状态映射 |
| `McpRetryTimeoutTests.cs` | 重试逻辑、超时处理、退避公式 |
| `McpAuthInjectionTests.cs` | URL 中 key 参数注入验证 |
| `McpEndToEndTests.cs` | 端到端流程测试 |

测试辅助工具（`TestHelpers/`）：
- `FakeMcpServer.cs` — 模拟 HttpMessageHandler 的伪网关
- `MockHttpMessageHandler.cs` — 可编程响应
- `McpResponseExtensions.cs` — 响应解析扩展
- `TestFileGenerator.cs` — 测试文件生成器

---

## 关键类/文件

| 文件 | 关键类 | 职责 |
|---|---|---|
| `FileUploadServer.Mcp/Program.cs` | — | 入口：配置加载 → 主循环 |
| `FileUploadServer.Mcp/Server/McpServer.cs` | `McpServer` | 生命周期 + 方法分发 |
| `FileUploadServer.Mcp/Server/ToolDefinition.cs` | `ToolDefinition`, `ToolDefinitions` | 6 个工具元数据定义 |
| `FileUploadServer.Mcp/Server/CallToolResult.cs` | `CallToolResult` | tools/call 响应封装 |
| `FileUploadServer.Mcp/Services/FileToolHandlers.cs` | `FileToolHandlers` | 参数校验 + HTTP 调用 |
| `FileUploadServer.Mcp/Services/McpHttpClient.cs` | `McpHttpClient` | URL 构建 / 超时 / 重试 |
| `FileUploadServer.Mcp/Services/ErrorMapper.cs` | `ErrorMapper` | HTTP → MCP 错误码映射 |
| `FileUploadServer.Mcp/McpServerConfig.cs` | `McpServerConfig` | 配置模型 |
| `FileUploadServer.Mcp/McpLogger.cs` | `McpLogger` | 日志输出 |
| `FileUploadServer.Mcp/Protocol/JsonRpcRequest.cs` | `JsonRpcRequest` | JSON-RPC 请求解析 |
| `FileUploadServer.Mcp/Protocol/JsonRpcResponse.cs` | `JsonRpcResponse` | JSON-RPC 响应构建 |
| `FileUploadServer.Mcp/Protocol/JsonRpcError.cs` | `JsonRpcError` | 错误码常量定义 |
| `FileUploadServer.Mcp/Protocol/McpError.cs` | `McpError` | 业务异常（抛 → 转为错误响应） |
| `FileUploadServer.Mcp/Protocol/McpJson.cs` | `McpJson` | JSON 序列化配置 |
| `FileUploadServer.Mcp/Protocol/StdioTransport.cs` | `StdioTransport` | stdin/stdout 读写 |

---

## 关联文档

- [01-architecture.md](01-architecture.md) — 架构总览、DI 注册清单（MCP 作为独立项目）
- [02-api-reference.md](02-api-reference.md) — MCP 调用的 HTTP 端点详情
- [03-permission.md](03-permission.md) — Admin/Temporary 密钥权限体系
- [07-ws-storage.md](07-ws-storage.md) — WS 存储节点对 MCP 文件操作的底层支撑
- 旧参考：`doc/MCP-Development-Guide.md` — MCP 开发指南（本项目专属，含 MCP SDK 接入细节）
