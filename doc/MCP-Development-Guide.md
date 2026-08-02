# FileUploadServer MCP 接口开发文档

**版本**: 1.0
**协议基础**: JSON-RPC 2.0 over HTTP/SSE
**目标**: 将文件上传下载服务的完整能力（上传、下载、删除、鉴权）暴露为 MCP Tools，供 AI 代理调用。

---

## 目录

1. [第一部分：MCP 协议生命周期接口](#第一部分mcp-协议生命周期接口)
2. [第二部分：工具发现接口 — tools/list](#第二部分工具发现接口--toolslist)
3. [第三部分：业务执行接口 — tools/call](#第三部分业务执行接口--toolscall)
4. [第四部分：鉴权机制设计](#第四部分鉴权机制设计)
5. [第五部分：错误处理规范](#第五部分错误处理规范)
6. [第六部分：完整 Tool Schema 定义](#第六部分完整-tool-schema-定义)
7. [附录：检查清单](#附录检查清单)

---

## 第一部分：MCP 协议生命周期接口

无论业务是什么，以下 3 个接口是 MCP 握手的基石，**缺一不可**。

### 1. 初始化握手 (`initialize`)

客户端连接后调用的第一个方法，用于协商协议版本和能力。

- **Method**: `initialize`
- **请求参数**:
  ```json
  {
    "protocolVersion": "0.1.0",
    "clientInfo": { "name": "claude-code", "version": "1.0" },
    "capabilities": { "roots": { "listChanged": true } }
  }
  ```
- **必须响应**: 返回服务端支持的能力清单，`tools` 能力必须声明。
  ```json
  {
    "protocolVersion": "0.1.0",
    "serverInfo": {
      "name": "file-upload-server-mcp",
      "version": "1.0.0"
    },
    "capabilities": {
      "tools": { "listChanged": true }
    }
  }
  ```

### 2. 初始化完成通知 (`notifications/initialized`)

客户端收到 `initialize` 响应后发送此通知。服务端收到后正式接受业务工具调用。

- **Method**: `notifications/initialized`（Notification，无需 Response）

### 3. 关闭与退出

支持通过 `exit` 通知或 `SIGINT` 信号关闭 MCP 服务。**必须**确保释放数据库连接池（Npgsql）、HTTP 客户端等资源。

---

## 第二部分：工具发现接口 — `tools/list`

无参数调用，返回本服务暴露的全部文件管理工具定义。每个工具包含 `name`、`description` 和 `inputSchema`。

**实现要点**：
- `description` 必须极其详尽，包含使用场景、鉴权要求、注意事项 —— 这是 LLM 决定是否调用的唯一依据。
- `inputSchema` 严格遵循 **JSON Schema 7** 规范。

### 工具列表总览

| 工具名称 | 对应 API | 说明 | 鉴权 |
|:---|:---|:---|:---|
| `file_list` | `GET /api/files` | 获取文件列表 | API Key |
| `file_info` | `GET /api/files/{id}` | 获取单个文件详情 | API Key |
| `file_upload` | `POST /api/files` | 上传文件 | API Key |
| `file_download` | `GET /api/files/download/{id}` | 下载文件 | API Key |
| `file_delete` | `DELETE /api/files/{id}` | 删除文件 | API Key |
| `file_set_public` | `PUT /api/admin/files/{id}/public` | 设置文件公共访问 | API Key |

详细 Schema 定义见[第六部分](#第六部分完整-tool-schema-定义)。

---

## 第三部分：业务执行接口 — `tools/call`

接收 LLM 的参数，转化为真实 HTTP 请求，并返回标准 MCP 响应。

### 请求结构

```json
{
  "method": "tools/call",
  "params": {
    "name": "file_upload",
    "arguments": {
      "file_path": "/path/to/local/file.pdf",
      "remote_path": "/documents/report.pdf",
      "api_key": "a1b2c3d4e5f6..."
    }
  }
}
```

### 内部实现逻辑（硬性要求）

1. **参数校验**: 必须再次校验 `arguments` 是否符合 `inputSchema`，防止 LLM 幻觉产生非法参数。
2. **鉴权注入**: 从 `arguments.api_key` 中提取密钥，自动注入到下游 HTTP 请求的 `?key=` 查询参数。
3. **HTTP 转换**:
   - `file_upload` → `POST {base_url}/api/files`（multipart/form-data，文件二进制 + `path` 表单字段）
   - `file_download` → `GET {base_url}/api/files/download/{id}?key={api_key}`（流式接收，返回 Base64 或临时下载 URL）
   - `file_delete` → `DELETE {base_url}/api/files/{id}?key={api_key}`
   - `file_list` → `GET {base_url}/api/files?key={api_key}`
   - `file_info` → `GET {base_url}/api/files/{id}?key={api_key}`
4. **超时与重试**:
   - 上传/下载：**300s** 超时（大文件场景）
   - 其他操作：**30s** 超时
   - 5xx/超时自动重试最多 **2 次**（指数退避：1s → 3s）
5. **响应格式约束**: 必须固定为 `content` 数组形式。

### 标准返回格式

**成功**:
```json
{
  "content": [
    {
      "type": "text",
      "text": "{\"status\":\"success\",\"data\":{\"id\":42,\"file_name\":\"report.pdf\",\"file_size\":1024000,\"content_type\":\"application/pdf\",\"uploaded_at\":\"2026-08-02T10:30:00\"}}"
    }
  ],
  "isError": false
}
```

**失败**:
```json
{
  "content": [
    {
      "type": "text",
      "text": "业务逻辑拒绝：文件不属于当前密钥，无法删除（file_id=99, key_type=Temporary）"
    }
  ],
  "isError": true
}
```

> **特别重要**: 如果下游返回 4xx/5xx，**必须**设置 `"isError": true`，并在 `text` 中写入明确的错误码和业务语义。**不要透传底层 .NET 堆栈跟踪**。

---

## 第四部分：鉴权机制设计

### 4.1 现有鉴权体系回顾

本项目的鉴权体系分为四层：

```
┌─────────────────────────────────────────────────┐
│  请求到达                                        │
├─────────────────────────────────────────────────┤
│  Layer 1: 路径分流                               │
│  ├── /api/admin/*  → localhost 校验              │
│  ├── /api/public/* → IP 白名单校验                │
│  ├── /p/*          → PublicFileMiddleware 处理    │
│  └── /api/files/*  → 需要 API Key                │
├─────────────────────────────────────────────────┤
│  Layer 2: ApiKeyAuthMiddleware                   │
│  ├── 从 ?key= 查询参数或表单提取密钥               │
│  ├── 查询 ApiKeys 表验证有效性                    │
│  └── 将 ApiKey 对象存入 HttpContext.Items         │
├─────────────────────────────────────────────────┤
│  Layer 3: PermissionService                      │
│  ├── Admin 密钥 → 访问所有文件                    │
│  └── Temporary 密钥 → 仅访问自己上传的文件         │
├─────────────────────────────────────────────────┤
│  Layer 4: 文件级权限检查（CanAccessFileAsync）     │
│  └── 检查 file.ApiKeyId == currentKey.Id         │
└─────────────────────────────────────────────────┘
```

### 4.2 MCP 鉴权方案

MCP 协议本身不定义鉴权标准，需要自行设计。建议采用以下方案：

#### 方案 A：MCP Server 启动时配置 Master Key（推荐）

MCP Server 启动时通过环境变量或配置文件注入一个 **Admin 类型**的 Master API Key：

```bash
# 启动 MCP Server
FILE_SERVER_BASE_URL=https://your-server.com \
FILE_SERVER_MASTER_KEY=a1b2c3d4e5f6... \
  dotnet run --project FileUploadServer.Mcp
```

MCP Server 使用此 Master Key 调用所有后端 API，AI 代理**不需要**在每次调用时传递密钥。安全边界由 MCP 协议本身的传输层保障（stdio/本地 SSE）。

**优点**：
- AI 代理无需感知密钥细节
- 密钥不进入 LLM 上下文，避免泄露
- 实现简单

#### 方案 B：每个 Tool 接受 `api_key` 参数（灵活但需谨慎）

每个 Tool 的 `inputSchema` 中增加可选的 `api_key` 字段。如果传入则使用该密钥，否则使用配置的默认密钥。这允许 AI 代理以不同身份（Admin/Temporary）操作。

> ⚠️ **安全警告**: 如果采用方案 B，API 密钥将通过 LLM 上下文传递，存在泄露风险。仅建议在受控环境（如企业内部部署）使用。

#### 方案 C：MCP 独立认证（自建）

在 MCP Server 层维护自己的认证机制（JWT、OAuth2 等），MCP Server 作为网关，内部使用固定的服务账号调用后端 API。

> **本文档推荐方案 A**，后续 Schema 定义基于方案 A 设计。

### 4.3 MCP Server 中的鉴权实现

```csharp
// MCP Server 启动配置
public class McpServerConfig
{
    public string FileServerBaseUrl { get; set; } = "https://localhost:5001";
    public string MasterApiKey { get; set; } = "";  // Admin 类型密钥
    public int RequestTimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 2;
}

// HTTP 请求注入鉴权
public async Task<HttpResponseMessage> SendAuthenticatedRequest(
    HttpMethod method, string endpoint, HttpContent? content = null)
{
    var url = $"{_config.FileServerBaseUrl}{endpoint}";
    var uriBuilder = new UriBuilder(url);
    var query = HttpUtility.ParseQueryString(uriBuilder.Query);
    query["key"] = _config.MasterApiKey;
    uriBuilder.Query = query.ToString();

    var request = new HttpRequestMessage(method, uriBuilder.ToString())
    {
        Content = content
    };
    return await _httpClient.SendAsync(request);
}
```

---

## 第五部分：错误处理规范

MCP 接口在调用 Web 服务失败时，**必须**返回标准 JSON-RPC 错误码。

### 错误码映射表

| JSON-RPC 错误码 | 含义 | 下游触发场景 | 后端 HTTP 状态码 |
|:---|:---|:---|:---|
| **-32602** | 无效参数 | `arguments` 类型/必填校验失败、文件不存在 | 400 |
| **-32602** | 文件未找到 | 文件 ID 对应记录不存在 | 404 |
| **-32000** | 服务超时 | 下载/上传超过 300s，其他超过 30s | — |
| **-32001** | 服务不可达 | 连接拒绝、DNS 解析失败、SSL 错误 | — |
| **-32003** | 权限不足 | 文件不属于当前密钥 / 密钥过期 / 密钥无效 | 401 / 403 |
| **-32004** | 限流触发 | 后端返回 429 或被限流中间件拦截 | 429 |
| **-32005** | 存储不可用 | WS 存储节点离线 | 503 |
| **-32603** | 内部错误 | 未预期的服务器内部错误 | 500 |

### 错误返回示例

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32003,
    "message": "权限不足：当前密钥无权访问该文件（file_id=42, key_type=Temporary）",
    "data": {
      "http_status": 403,
      "retryable": false,
      "file_id": 42
    }
  }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "error": {
    "code": -32005,
    "message": "存储节点不可用：文件存储在远程 WS 客户端，但该客户端当前离线（client_id=ws-node-01）",
    "data": {
      "http_status": 503,
      "retryable": true,
      "retry_after_seconds": 30
    }
  }
}
```

### `isError` 判断逻辑（在 tools/call 响应中）

```csharp
private bool IsErrorStatus(int httpStatusCode) => httpStatusCode switch
{
    >= 200 and < 300 => false,
    400 or 404 => true,   // 客户端错误 → isError: true
    401 or 403 => true,   // 鉴权/权限错误 → isError: true
    429 => true,          // 限流 → isError: true
    503 => true,          // 存储不可用 → isError: true
    >= 500 => true,       // 服务器错误 → isError: true
    _ => true
};
```

---

## 第六部分：完整 Tool Schema 定义

### 6.1 `file_list` — 获取文件列表

```json
{
  "name": "file_list",
  "description": "获取当前 API 密钥可访问的文件列表。Admin 密钥返回所有文件，Temporary 密钥仅返回自己上传的文件。结果按上传时间倒序排列。注意：此操作仅返回文件元数据（名称、大小、类型等），不包含文件内容。",
  "inputSchema": {
    "type": "object",
    "properties": {},
    "required": []
  }
}
```

### 6.2 `file_info` — 获取单个文件详情

```json
{
  "name": "file_info",
  "description": "根据文件 ID 获取单个文件的详细信息（元数据）。返回文件名、大小、MIME 类型、上传时间、存储模式、是否公开等信息。注意：不包含文件内容，如需获取文件内容请使用 file_download。",
  "inputSchema": {
    "type": "object",
    "properties": {
      "file_id": {
        "type": "integer",
        "description": "文件 ID，从 file_list 返回结果中获取"
      }
    },
    "required": ["file_id"]
  }
}
```

### 6.3 `file_upload` — 上传文件

```json
{
  "name": "file_upload",
  "description": "上传一个文件到服务器。支持本地磁盘存储和 WebSocket 远程存储（根据路径前缀自动路由）。如果服务器启用了加密，文件会被 AES-256-GCM 透明加密后存储。最大支持 1GB 单文件。\n\n使用场景：当用户要求保存、存储或上传文件时使用。\n\n注意事项：\n- remote_path 以 / 开头，如 /documents/report.pdf\n- 如果 remote_path 匹配某个 WS 存储节点的路径前缀，文件将转发到该远程节点\n- 上传成功后返回文件元数据，包含可用于后续操作的 file_id",
  "inputSchema": {
    "type": "object",
    "properties": {
      "local_file_path": {
        "type": "string",
        "description": "本地文件的绝对路径，MCP Server 将读取此文件并上传"
      },
      "remote_path": {
        "type": "string",
        "description": "服务器上的存储路径，如 /documents/report.pdf。以 / 开头。如果不指定则使用原始文件名存储在根目录。"
      }
    },
    "required": ["local_file_path"]
  }
}
```

### 6.4 `file_download` — 下载文件

```json
{
  "name": "file_download",
  "description": "根据文件 ID 下载文件内容。服务器会透明解密（如果文件是加密存储的），并以流式方式返回。支持从本地磁盘和 WebSocket 远程存储节点下载。\n\n使用场景：当用户要求获取、查看或下载文件内容时使用。\n\n注意事项：\n- 大文件下载可能需要较长时间（最大 300s 超时）\n- 下载的文件内容以 Base64 编码返回\n- 如果文件存储在离线 WS 节点上，操作将失败并返回 -32005 错误\n- 返回的 mime_type 可用于判断如何处理文件内容",
  "inputSchema": {
    "type": "object",
    "properties": {
      "file_id": {
        "type": "integer",
        "description": "要下载的文件 ID"
      }
    },
    "required": ["file_id"]
  }
}
```

### 6.5 `file_delete` — 删除文件

```json
{
  "name": "file_delete",
  "description": "根据文件 ID 删除文件。此操作会同时删除数据库记录和物理文件（本地磁盘或 WS 远程节点）。\n\n使用场景：当用户要求删除、移除或清理文件时使用。\n\n注意事项：\n- 删除操作不可逆，请确认后再执行\n- 只能删除当前 API 密钥有权访问的文件\n- 如果文件存储在 WS 远程节点且该节点离线，本地数据库记录仍会被删除，但远程文件可能残留\n- 加密文件的子目录结构也会被正确清理",
  "inputSchema": {
    "type": "object",
    "properties": {
      "file_id": {
        "type": "integer",
        "description": "要删除的文件 ID"
      }
    },
    "required": ["file_id"]
  }
}
```

### 6.6 `file_set_public` — 设置文件公共访问

```json
{
  "name": "file_set_public",
  "description": "设置文件的公共访问标记。设为公开后，文件可通过 /p/{public_path} 路径匿名访问（需满足 IP 白名单、限流等条件）。取消公开后，文件只能通过 API Key 访问。\n\n使用场景：需要分享文件给没有 API 密钥的外部用户时使用。\n\n注意事项：\n- 需要 Admin 类型的 API 密钥\n- public_path 是公开访问的唯一路径标识，如 /shared/image.jpg\n- 取消公开时设置 is_public=false 即可\n- 公开文件受 IP 白名单/黑名单、限流、文件大小限制等多重保护",
  "inputSchema": {
    "type": "object",
    "properties": {
      "file_id": {
        "type": "integer",
        "description": "要设置公开访问的文件 ID"
      },
      "is_public": {
        "type": "boolean",
        "description": "是否公开：true=设为公开，false=取消公开"
      },
      "public_path": {
        "type": "string",
        "description": "公开访问路径，如 /shared/report.pdf。仅当 is_public=true 时需要。"
      }
    },
    "required": ["file_id", "is_public"]
  }
}
```

---

## 第七部分：MCP Server 实现架构

### 7.1 项目结构建议

```
FileUploadServer.Mcp/                  # 新增的 MCP Server 项目
├── Program.cs                         # 启动入口
├── McpServerConfig.cs                 # 配置类
├── Services/
│   ├── McpHttpClient.cs              # 封装对后端 API 的 HTTP 调用
│   ├── FileToolHandlers.cs           # 文件操作（上传/下载/删除/列表/公开设置）
│   └── AuthService.cs                # 鉴权服务（如果有独立鉴权需求）
├── Models/
│   ├── FileItemDto.cs                # 文件信息 DTO
│   └── ApiKeyDto.cs                  # 密钥信息 DTO
├── appsettings.json                  # 配置文件
└── McpServer.csproj
```

### 7.2 启动配置（appsettings.json）

```json
{
  "McpServer": {
    "FileServerBaseUrl": "https://localhost:5001",
    "MasterApiKey": "",
    "RequestTimeoutSeconds": 300,
    "MaxRetries": 2
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### 7.3 核心实现伪代码 — `tools/call` 路由

```csharp
public async Task<JsonRpcResponse> HandleToolsCall(JsonRpcRequest request)
{
    var toolName = request.Params.Name;
    var arguments = request.Params.Arguments;

    return toolName switch
    {
        "file_list"         => await HandleFileList(),
        "file_info"         => await HandleFileInfo(arguments),
        "file_upload"       => await HandleFileUpload(arguments),
        "file_download"     => await HandleFileDownload(arguments),
        "file_delete"       => await HandleFileDelete(arguments),
        "file_set_public"   => await HandleFileSetPublic(arguments),
        _ => throw new McpError(-32601, $"Unknown tool: {toolName}")
    };
}

private async Task<JsonRpcResponse> HandleFileUpload(JObject args)
{
    // 1. 参数校验
    var localPath = args["local_file_path"]?.Value<string>();
    if (string.IsNullOrEmpty(localPath))
        throw new McpError(-32602, "缺少必填参数: local_file_path");
    if (!File.Exists(localPath))
        throw new McpError(-32602, $"文件不存在: {localPath}");

    var remotePath = args["remote_path"]?.Value<string>();

    // 2. 构建 multipart 请求
    using var form = new MultipartFormDataContent();
    using var fileStream = File.OpenRead(localPath);
    var fileContent = new StreamContent(fileStream);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue(
        MimeTypes.GetMimeType(localPath));
    form.Add(fileContent, "file", Path.GetFileName(localPath));
    if (!string.IsNullOrEmpty(remotePath))
        form.Add(new StringContent(remotePath), "path");

    // 3. 发送请求（带鉴权、超时、重试）
    var response = await SendWithRetry(
        HttpMethod.Post, "/api/files", form,
        TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));

    // 4. 转换响应
    var result = await response.Content.ReadAsStringAsync();
    return new JsonRpcResponse
    {
        Content = [new ContentItem { Type = "text", Text = result }],
        IsError = !response.IsSuccessStatusCode
    };
}
```

### 7.4 重试与超时实现

```csharp
private async Task<HttpResponseMessage> SendWithRetry(
    HttpMethod method, string endpoint, HttpContent? content, TimeSpan timeout)
{
    var maxRetries = _config.MaxRetries;
    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var request = BuildAuthenticatedRequest(method, endpoint, content);
            var response = await _httpClient.SendAsync(request, cts.Token);

            // 5xx 才重试，4xx 直接返回
            if ((int)response.StatusCode < 500 || attempt == maxRetries)
                return response;

            // 指数退避：1s → 3s
            var delay = TimeSpan.FromSeconds(Math.Pow(3, attempt));
            _logger.LogWarning("请求失败 (attempt {Attempt}/{Max}), {Status}, {Delay}s 后重试",
                attempt + 1, maxRetries, response.StatusCode, delay.TotalSeconds);
            await Task.Delay(delay, cts.Token);
        }
        catch (TaskCanceledException)
        {
            if (attempt == maxRetries)
                throw new McpError(-32000, $"请求超时 ({timeout.TotalSeconds}s): {method} {endpoint}");
        }
        catch (HttpRequestException ex)
        {
            if (attempt == maxRetries)
                throw new McpError(-32001, $"服务不可达: {ex.Message}");
        }
    }
    throw new McpError(-32001, "重试耗尽，服务不可达");
}
```

---

## 第八部分：单元测试计划

MCP Server 开发完成后，需要覆盖以下测试。项目已集成 xUnit（`FileUploadServer.Tests`），所有测试用例放到该项目的 `Mcp/` 目录下。

### 8.1 协议生命周期测试

| 测试ID | 测试名称 | 输入 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|
| `LIFE-01` | `initialize` 返回正确能力声明 | 标准 initialize 请求 | `capabilities.tools.listChanged = true`，`serverInfo` 非空 | 单元 |
| `LIFE-02` | `initialize` 拒绝不兼容协议版本 | `protocolVersion: "999.0.0"` | 返回错误，服务端不支持该版本 | 单元 |
| `LIFE-03` | `initialized` 通知后接受工具调用 | 先 initialize → initialized，再调用 tools/list | tools/list 正常返回工具列表 | 集成 |
| `LIFE-04` | 未 `initialized` 直接调用 tools/list | 略过 initialized 直接 tools/list | 返回错误 `-32002`（未初始化） | 单元 |
| `LIFE-05` | `shutdown` 后释放资源 | 发送 shutdown 通知 | `HttpClient` 已释放，数据库连接已关闭 | 单元 |

### 8.2 工具发现测试 — `tools/list`

| 测试ID | 测试名称 | 输入 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|
| `LIST-01` | 返回 6 个工具 | tools/list 请求 | 返回数组长度 = 6，每个包含 `name`/`description`/`inputSchema` | 单元 |
| `LIST-02` | 工具名与文档一致 | tools/list 请求 | 名称为 `file_list`、`file_info`、`file_upload`、`file_download`、`file_delete`、`file_set_public` | 单元 |
| `LIST-03` | inputSchema 符合 JSON Schema 7 | tools/list 请求 | 每个工具 schema 可通过 `JsonSchema.FromText()` 解析，`required` 字段与文档定义一致 | 单元 |
| `LIST-04` | description 非空且有意义 | tools/list 请求 | 每个 description 长度 > 20 字符，包含使用场景说明 | 单元 |

### 8.3 文件操作工具测试 — `tools/call`

#### file_list

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `FL-01` | 正常获取文件列表 | `{ }` | 后端返回 200 + 文件数组 | `content[0].text` 包含文件 JSON，`isError: false` | 单元 |
| `FL-02` | Admin 密钥返回全部文件 | `{ }` + Admin Key | 后端返回 200 + 3 条记录 | 不解包过滤，透传后端响应 | 单元 |
| `FL-03` | 后端返回 401（密钥无效） | `{ }` | 后端返回 401 | `isError: true`，text 含"密钥无效"业务语义 | 单元 |

#### file_info

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `FI-01` | 正常获取文件详情 | `{"file_id": 1}` | 后端返回 200 + 文件 JSON | `isError: false`，包含 `file_name`/`file_size` 等字段 | 单元 |
| `FI-02` | 文件不存在 | `{"file_id": 99999}` | 后端返回 404 | `isError: true`，`error.code = -32602` | 单元 |
| `FI-03` | 缺少必填参数 | `{ }` | 不发送请求 | `error.code = -32602`，"缺少必填参数: file_id" | 单元 |
| `FI-04` | 参数类型错误 | `{"file_id": "abc"}` | 不发送请求 | `error.code = -32602`，"参数类型错误" | 单元 |

#### file_upload

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `UP-01` | 正常上传文件 | `{"local_file_path": "/tmp/test.pdf"}` | 后端返回 201 + 文件元数据 | `isError: false`，返回 `file_id` | 单元 |
| `UP-02` | 本地文件不存在 | `{"local_file_path": "/tmp/notexist.pdf"}` | 不发送请求 | `error.code = -32602`，"文件不存在: /tmp/notexist.pdf" | 单元 |
| `UP-03` | 缺少 local_file_path | `{ }` | 不发送请求 | `error.code = -32602`，"缺少必填参数: local_file_path" | 单元 |
| `UP-04` | 指定 remote_path | `{"local_file_path": "/tmp/test.pdf", "remote_path": "/docs/report.pdf"}` | 后端返回 201 | multipart body 中 `path` 字段 = `/docs/report.pdf` | 单元 |
| `UP-05` | 后端返回 413（文件过大） | 上传 2GB 文件 | 后端返回 413 | `isError: true`，明确提示文件大小超限 | 单元 |

#### file_download

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `DL-01` | 正常下载文件 | `{"file_id": 1}` | 后端返回 200 + 文件流 | `isError: false`，content 包含 Base64 编码的文件内容 | 单元 |
| `DL-02` | 文件不存在 | `{"file_id": 99999}` | 后端返回 404 | `isError: true`，`error.code = -32602` | 单元 |
| `DL-03` | WS 存储节点离线 | `{"file_id": 5}` | 后端返回 503 | `isError: true`，`error.code = -32005`，标记 `retryable: true` | 单元 |
| `DL-04` | 大文件下载超时 | `{"file_id": 1}` | 后端延迟 > 300s | `error.code = -32000`，"请求超时 (300s)" | 单元 |

#### file_delete

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `DEL-01` | 正常删除文件 | `{"file_id": 1}` | 后端返回 204 | `isError: false` | 单元 |
| `DEL-02` | 文件不属于当前密钥 | `{"file_id": 99}` + Temp Key | 后端返回 403 | `isError: true`，`error.code = -32003`，提示权限不足 | 单元 |
| `DEL-03` | 文件不存在 | `{"file_id": 99999}` | 后端返回 404 | `isError: true`，`error.code = -32602` | 单元 |

#### file_set_public

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `PUB-01` | 设置文件为公开 | `{"file_id": 1, "is_public": true, "public_path": "/shared/doc.pdf"}` | 后端返回 200 | `isError: false` | 单元 |
| `PUB-02` | 取消公开 | `{"file_id": 1, "is_public": false}` | 后端返回 200 | `isError: false` | 单元 |
| `PUB-03` | 设为公开但缺少 public_path | `{"file_id": 1, "is_public": true}` | 不发送请求 | `error.code = -32602`，"is_public=true 时必须提供 public_path" | 单元 |
| `PUB-04` | 使用 Temporary 密钥操作 | `{...}` + Temp Key | 后端返回 403 | `isError: true`，`error.code = -32003`，提示需要 Admin 密钥 | 单元 |

### 8.4 鉴权注入测试

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `AUTH-01` | Master Key 自动注入到所有请求 | 调用任意工具 | 检查请求 URL | `?key={master_key}` 出现在 Query 中 | 单元 |
| `AUTH-02` | 未配置 Master Key 时启动失败 | 环境变量 `FILE_SERVER_MASTER_KEY` 为空 | 不发送请求 | 启动时抛出 `InvalidOperationException` | 单元 |
| `AUTH-03` | 密钥过期后请求失败 | Master Key 已过期 | 后端返回 401 | `isError: true`，`error.code = -32003` | 单元 |

### 8.5 重试与超时测试

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期结果 | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `RETRY-01` | 5xx 触发重试并最终成功 | `file_list` | 第 1 次 503 → 第 2 次 200 | 共发送 2 次请求，最终 `isError: false` | 单元 |
| `RETRY-02` | 3 次全部 5xx 返回失败 | `file_list` | 连续 3 次 503 | 共发送 3 次请求，`isError: true`，`error.code = -32001` | 单元 |
| `RETRY-03` | 4xx 不触发重试 | `file_info` | 后端返回 403 | 仅发送 1 次请求，立即返回 `isError: true` | 单元 |
| `RETRY-04` | 重试间隔符合指数退避 | `file_list` | 连续 5xx | 第 1 次重试间隔 ≈ 1s，第 2 次 ≈ 3s | 单元 |
| `RETRY-05` | 上传超时不重试（300s 已是最长） | `file_upload` | 后端延迟 310s | 超时即失败，`error.code = -32000` | 单元 |

### 8.6 错误码映射测试

| 测试ID | 测试名称 | 输入 | Mock 后端 | 预期 JSON-RPC Error Code | 测试级别 |
|:---|:---|:---|:---|:---|:---|
| `ERR-01` | 400 → -32602 | 参数非法 | 后端返回 400 | `error.code = -32602` | 单元 |
| `ERR-02` | 401 → -32003 | 密钥无效 | 后端返回 401 | `error.code = -32003` | 单元 |
| `ERR-03` | 403 → -32003 | 权限不足 | 后端返回 403 | `error.code = -32003` | 单元 |
| `ERR-04` | 429 → -32004 | 触发限流 | 后端返回 429 | `error.code = -32004`，含 `retry_after_seconds` | 单元 |
| `ERR-05` | 503 → -32005 | WS 存储不可用 | 后端返回 503 | `error.code = -32005` | 单元 |
| `ERR-06` | 连接拒绝 → -32001 | 后端不可达 | 直接抛 `HttpRequestException` | `error.code = -32001` | 单元 |

### 8.7 端到端集成测试（需真实后端）

| 测试ID | 测试名称 | 测试步骤 | 预期结果 |
|:---|:---|:---|:---|
| `E2E-01` | 完整上传→下载→删除流程 | 1. 上传一个小文本文件 2. 用返回的 file_id 下载验证内容一致 3. 删除文件 4. 用同一 file_id 查 info 确认 404 | 内容一致，删除后查不到 |
| `E2E-02` | Temporary 密钥隔离 | 1. Admin 上传文件 A 2. Temp 密钥上传文件 B 3. Temp 密钥 list（只看得到 B）4. Temp 密钥尝试删 A（403） | Temp 密钥看不到/删不了 Admin 的文件 |
| `E2E-03` | 公开文件匿名访问 | 1. 上传文件 2. file_set_public 设为公开 3. 通过 HTTP GET /p/{path} 访问 4. file_set_public 取消公开 5. 再次访问（404） | 公开可访问，取消后不可访问 |

### 8.8 测试文件结构

```
FileUploadServer.Tests/
├── Mcp/
│   ├── McpLifecycleTests.cs           # LIFE-01 ~ LIFE-05
│   ├── McpToolsListTests.cs           # LIST-01 ~ LIST-04
│   ├── McpFileListTests.cs            # FL-01 ~ FL-03
│   ├── McpFileInfoTests.cs            # FI-01 ~ FI-04
│   ├── McpFileUploadTests.cs          # UP-01 ~ UP-05
│   ├── McpFileDownloadTests.cs        # DL-01 ~ DL-04
│   ├── McpFileDeleteTests.cs          # DEL-01 ~ DEL-03
│   ├── McpFileSetPublicTests.cs       # PUB-01 ~ PUB-04
│   ├── McpAuthInjectionTests.cs       # AUTH-01 ~ AUTH-03
│   ├── McpRetryTimeoutTests.cs        # RETRY-01 ~ RETRY-05
│   ├── McpErrorMappingTests.cs        # ERR-01 ~ ERR-06
│   └── McpEndToEndTests.cs            # E2E-01 ~ E2E-03（需真实后端）
├── TestHelpers/
│   ├── MockHttpMessageHandler.cs       # Mock HTTP 响应
│   ├── FakeMcpServer.cs                # 测试用 MCP Server 工厂
│   └── TestFileGenerator.cs           # 生成测试文件
└── FileUploadServer.Tests.csproj      # 添加 McpServer 项目引用
```

---

## 附录 A：检查清单

在开发完成时，请逐一核对以下功能：

- [ ] 实现了 `initialize` 并正确声明 `tools` 能力。
- [ ] 正确处理了 `notifications/initialized` 通知。
- [ ] `tools/list` 返回了完整的 6 个工具定义，每个工具包含详细的 `description` 和严格的 `inputSchema`。
- [ ] `tools/call` 包含了参数校验、HTTP 转发、鉴权注入。
- [ ] 文件上传实现了 300s 超时，其他操作 30s 超时。
- [ ] 所有可重试的错误（5xx、超时、连接失败）实现了最多 2 次指数退避重试。
- [ ] `tools/call` 的返回结果严格包裹在 `content[].text` 中，`isError` 正确设置。
- [ ] 所有异常均映射为 `-32602` / `-32xxx` 范围内的 JSON-RPC 错误码，未透传 .NET 堆栈。
- [ ] MCP Server 启动配置支持环境变量 `FILE_SERVER_BASE_URL` 和 `FILE_SERVER_MASTER_KEY`。
- [ ] 添加了结构化日志记录（每次调用的 tool_name、耗时、成功/失败状态）。
- [ ] 在 `shutdown` / `exit` 时正确释放 `HttpClient` 和数据库连接。

---

## 附录 B：MCP 客户端配置示例（Claude Code）

在 Claude Code 中使用此 MCP Server 的配置：

```json
{
  "mcpServers": {
    "file-upload-server": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/FileUploadServer.Mcp/FileUploadServer.Mcp.csproj"
      ],
      "env": {
        "FILE_SERVER_BASE_URL": "https://your-file-server.com",
        "FILE_SERVER_MASTER_KEY": "your-admin-api-key-here"
      }
    }
  }
}
```

---

> **下一步建议**: 如果条件允许，可编写一个从现有 Swagger/OpenAPI 文档自动生成 `tools/list` Schema 的脚本（项目已启用 Swagger，访问 `/swagger/v1/swagger.json` 即可获取完整的 OpenAPI 规范），这将节省约 80% 的工具定义工作量。
