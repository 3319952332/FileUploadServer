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
        var request = MakeRequest(id, "initialize", $"{{\"protocolVersion\":\"{protocolVersion}\"}}");
        return (await Server.HandleAsync(request))!;
    }

    public async Task SendInitializedAsync()
    {
        var request = MakeRequest(null, "notifications/initialized");
        await Server.HandleAsync(request);
    }

    public async Task<JsonRpcResponse> CallToolAsync(string name, string argsJson = "{}", long id = 10)
    {
        var request = MakeRequest(id, "tools/call", $"{{\"name\":\"{name}\",\"arguments\":{argsJson}}}");
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

    private static JsonRpcRequest MakeRequest(long? id, string method, string paramsJson = "{}")
    {
        var idPart = id.HasValue ? $"\"id\":{id.Value}," : string.Empty;
        var json = $"{{\"jsonrpc\":\"2.0\",{idPart}\"method\":\"{method}\",\"params\":{paramsJson}}}";
        return JsonRpcRequest.TryParse(json, out _)!;
    }
}
