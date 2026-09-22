using System.Text.Json.Nodes;
using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Server;
using FileUploadServer.Mcp.Services;

namespace FileUploadServer.Tests.Mcp.TestHelpers;

/// <summary>
/// 测试用 MCP Server 工厂：内嵌 MockHttpMessageHandler + 配置，
/// 直接驱动 McpServer.HandleAsync，无需真实 stdio。
/// </summary>
public sealed class FakeMcpServer : IDisposable
{
    public MockHttpMessageHandler HttpHandler { get; } = new();
    public McpServerConfig Config { get; }
    public McpHttpClient HttpClient { get; }
    public McpServer Server { get; }

    public FakeMcpServer(string masterKey = "test-master-key", string baseUrl = "http://backend.test", TimeSpan? retryBaseDelay = null)
    {
        Config = new McpServerConfig
        {
            FileServerBaseUrl = baseUrl,
            MasterApiKey = masterKey,
            MaxRetries = 2,
            RequestTimeoutSeconds = 300,
            ShortRequestTimeoutSeconds = 30,
        };
        HttpClient = new McpHttpClient(Config, HttpHandler, retryBaseDelay ?? TimeSpan.Zero);
        Server = new McpServer(new FileToolHandlers(HttpClient));
    }

    // ---------------------------------------------------------- 协议辅助
    // 以下请求均带 id，Server 必有响应，故返回非空 JsonRpcResponse。
    public async Task<JsonRpcResponse> InitializeAsync(string protocolVersion = "2025-03-26", long id = 1)
    {
        var request = MakeRequest(id, "initialize",
            new JsonObject { ["protocolVersion"] = protocolVersion });
        return (await Server.HandleAsync(request))!;
    }

    public async Task SendInitializedAsync()
    {
        var request = MakeRequest(null, "notifications/initialized");
        await Server.HandleAsync(request);
    }

    /// <summary>调用工具。argsJson 必须是合法 JSON 对象文本（Windows 路径请改用 JsonObject 重载）。</summary>
    public async Task<JsonRpcResponse> CallToolAsync(string name, string argsJson = "{}", long id = 10)
    {
        return await CallToolAsync(name, JsonNode.Parse(argsJson)?.AsObject(), id);
    }

    /// <summary>调用工具（结构化参数，经序列化构造，避免手拼 JSON 的转义问题）。</summary>
    public async Task<JsonRpcResponse> CallToolAsync(string name, JsonObject? args, long id = 10)
    {
        var payload = new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() };
        var request = MakeRequest(id, "tools/call", payload);
        return (await Server.HandleAsync(request))!;
    }

    public async Task<JsonRpcResponse> ListToolsAsync(long id = 2)
    {
        var request = MakeRequest(id, "tools/list");
        return (await Server.HandleAsync(request))!;
    }

    /// <summary>标准初始化序列：initialize → notifications/initialized。</summary>
    public async Task InitializeAndNotifyAsync(string protocolVersion = "2025-03-26")
    {
        await InitializeAsync(protocolVersion);
        await SendInitializedAsync();
    }

    public void Dispose() => HttpClient.Dispose();

    private static JsonRpcRequest MakeRequest(long? id, string method, JsonNode? paramsNode = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = paramsNode ?? new JsonObject(),
        };
        if (id.HasValue)
        {
            obj["id"] = id.Value;
        }
        // 解析失败必须显式抛异常，让测试构造错误在源头暴露（此前用 ! 吞掉导致 NullReferenceException）
        return JsonRpcRequest.TryParse(obj.ToJsonString(), out _)
            ?? throw new InvalidOperationException($"测试构造的 JSON-RPC 请求无法解析: {method}");
    }
}
