为 Web 服务开发 MCP (Model Context Protocol) 接口，核心在于将您的 HTTP API 封装为 LLM 可调用的“工具”。MCP 规范严格依赖 JSON-RPC 2.0，但实际业务开发中，**真正“必须实现”的接口分为两层**：**协议握手层**（必须严格按规范实现）和**业务工具层**（按您的 Web 服务能力裁剪）。

以下是我为您整理的 **MCP 服务端必须实现的接口文档（Baseline Specification）**。

---

# MCP Web 服务接口开发规范（必须实现清单）

**版本**: 1.0
**协议基础**: JSON-RPC 2.0 over stdio / SSE
**目标**: 将 RESTful/GraphQL 业务能力无损暴露给 AI 代理。

---

## 第一部分：协议生命周期接口（强制核心）

无论您的 Web 服务业务是什么，以下 3 个接口是 MCP 握手与初始化的基石，**缺一不可**。

### 1. 初始化握手 (`initialize`)
客户端连接后调用的第一个方法，用于协商协议版本和能力。

- **Method**: `initialize`
- **请求参数**:
  ```json
  {
    "protocolVersion": "0.1.0",
    "clientInfo": { "name": "client-name", "version": "1.0" },
    "capabilities": { "roots": { "listChanged": true } }
  }
  ```
- **必须响应**: 返回服务端支持的能力清单，特别是 **`tools`** 能力必须声明为 `true`。
  ```json
  {
    "protocolVersion": "0.1.0",
    "serverInfo": { "name": "your-web-service-mcp", "version": "1.0.0" },
    "capabilities": {
      "tools": { "listChanged": true }  // 必须为 true，代表支持工具调用
    }
  }
  ```

### 2. 初始化完成通知 (`initialized`)
客户端收到初始化响应后，必须发送此通知。服务端需监听此信号，之后才正式接受业务工具调用。

- **Method**: `initialized` (Notification，无需 Response)。

### 3. 优雅关闭 (`shutdown` 或 退出)
支持通过 `exit` 通知或 `SIGINT` 信号关闭服务，**必须**确保释放 Web 服务持有的数据库连接池和 HTTP 长连接。

---

## 第二部分：工具发现接口（必须实现）

让 AI 知道您的 Web 服务能干什么。**这是 MCP 区别于普通 API 网关的核心接口**。

### 4. 获取工具列表 (`tools/list`)
无参数调用，必须返回当前 Web 服务暴露的所有业务接口定义（相当于 OpenAPI 的压缩版）。

- **Method**: `tools/list`
- **必须返回结构**: 数组，每一项必须包含 `name`、`description` 和 `inputSchema`。
- **强制要求**:
  - `description` 必须极其详尽（包含使用场景、注意事项），因为这是 LLM 决定是否调用的唯一依据。
  - `inputSchema` 必须严格遵循 **JSON Schema 7** 规范。

**必须包含的示例定义**（请替换为您的真实业务）:
```json
{
  "tools": [
    {
      "name": "web_service_requester",
      "description": "通用的 Web 服务请求工具，用于调用后端业务接口。当用户需要查询数据、提交表单或执行操作时使用。注意：只支持 application/json 格式。",
      "inputSchema": {
        "type": "object",
        "properties": {
          "endpoint": { "type": "string", "description": "API 路径，如 /api/v1/users" },
          "method": { "type": "string", "enum": ["GET", "POST", "PUT", "DELETE"] },
          "body": { "type": "object", "description": "请求负载（GET 时忽略）" },
          "headers": { "type": "object", "description": "覆盖默认请求头" }
        },
        "required": ["endpoint", "method"]
      }
    }
    // 如果您的服务有特定业务（如：订单创建、库存查询），建议将每个业务封装为一个独立的 tool，而不是通用 requester，这样 AI 调用准确率更高。
  ]
}
```

---

## 第三部分：业务执行接口（必须实现）

### 5. 调用工具 (`tools/call`)
接收 LLM 的参数，转化为真实的 Web 请求（HTTP Client），并返回标准结果。

- **Method**: `tools/call`
- **请求结构**:
  ```json
  {
    "name": "web_service_requester",
    "arguments": {
      "endpoint": "/api/v1/orders",
      "method": "POST",
      "body": { "product_id": 123, "quantity": 2 }
    }
  }
  ```
- **必须实现的内部逻辑（硬性要求）**:
  1. **参数校验**: 必须再次校验 `arguments` 是否符合 `inputSchema`，防止 LLM 幻觉。
  2. **HTTP 转换**: 将 `arguments` 映射为底层 Web 服务的 Request（拼接 URL、设置 Headers、序列化 Body）。
  3. **重试与超时**: 必须设置 **30s** 超时，并实现 **最多 2 次** 重试机制（仅针对 5xx/超时）。
  4. **响应格式约束**: 无论底层 Web 服务返回什么，MCP 响应必须固定为 `content` 数组形式。

- **必须返回的标准格式**:
  ```json
  {
    "content": [
      {
        "type": "text",
        "text": "{\"status\":\"success\",\"data\":{\"order_id\":\"ORD-2026-001\",\"total\":199.00}}"
      }
    ],
    "isError": false
  }
  ```
  - **特别重要**: 如果 Web 服务返回 4xx/5xx，**必须**设置 `"isError": true`，并在 `text` 中写入明确的错误码和报错信息（不要透传底层 Java/Python 堆栈，需转换为业务语义）。

---

## 第四部分：扩展增强接口（强烈建议实现）

虽然规范中为“可选”，但对于生产级 Web 服务，以下 2 个接口**建议强制实现**以提升 AI 体验。

### 6. 资源列表 (`resources/list`) —— 建议实现
如果您的 Web 服务有静态文档、公共配置或只读数据（如：国家编码表、支付渠道列表），建议通过资源暴露，而非工具。

```json
{
  "resources": [
    {
      "uri": "web-service://config/payment-channels",
      "name": "支付渠道配置",
      "mimeType": "application/json",
      "description": "当前启用的支付渠道列表，仅供查询"
    }
  ]
}
```

### 7. 读取资源 (`resources/read`)
提供具体的静态数据内容。

---

## 第五部分：必须实现的错误处理规范（Error Codes）

您的 MCP 接口在调用 Web 服务失败时，**必须**返回标准 JSON-RPC 错误码，而不是 HTTP 状态码。

| 错误码 (Code) | 含义 | 触发场景 |
| :--- | :--- | :--- |
| **-32602** | 无效参数 | AI 传入的 `arguments` 类型/必填校验失败 |
| **-32000** | Web 服务超时 | 下游 HTTP 接口响应 > 30s |
| **-32001** | Web 服务不可达 | 连接拒绝、SSL 错误、DNS 解析失败 |
| **-32003** | 业务逻辑拒绝 | 下游返回 403（权限不足）或 409（冲突） |
| **-32004** | 限流熔断 | 触发后端限流，返回 429 |

**错误返回示例**:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32003,
    "message": "业务逻辑拒绝：该订单已支付，无法取消",
    "data": { "http_status": 409, "retryable": false }
  }
}
```

---

## 文档总结：硬性检查清单（Checklist）

在开发完成时，请逐一核对以下功能是否具备：

- [ ] 实现了 `initialize` 并正确声明 `tools` 能力。
- [ ] 正确处理了 `initialized` 通知。
- [ ] `tools/list` 返回了完整的业务接口描述和严格的 JSON Schema。
- [ ] `tools/call` 包含了参数校验、HTTP 转发、超时（30s）与重试（2次）。
- [ ] `tools/call` 的返回结果严格包裹在 `content[].text` 中。
- [ ] 所有异常均映射为 **-32602** 或 **-32xxx** 范围内的 JSON-RPC 错误码，未透传堆栈。
- [ ] （建议）实现了 `resources/list` 和 `resources/read` 以承载静态配置。

---

**下一步行动建议**：
如果您的 Web 服务已有 Swagger/OpenAPI 文档，建议编写一个 **自动转换脚本**，将 OpenAPI 的 `paths` 直接批量生成 `tools/list` 的 `inputSchema`，这将节省 80% 的开发工作量。

如果需要针对您具体的业务（如“用户登录”、“订单查询”）定制具体的 Tool Schema，我可以帮您细化。请补充您的业务场景。