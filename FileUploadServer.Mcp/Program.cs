using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Server;
using FileUploadServer.Mcp.Services;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// 配置加载：appsettings.json（McpServer 节）→ 环境变量 FILE_SERVER_BASE_URL /
// FILE_SERVER_MASTER_KEY 覆盖。
// ---------------------------------------------------------------------------
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var config = new McpServerConfig();
configuration.GetSection("McpServer").Bind(config);

config.FileServerBaseUrl = Environment.GetEnvironmentVariable("FILE_SERVER_BASE_URL") ?? config.FileServerBaseUrl;
config.MasterApiKey = Environment.GetEnvironmentVariable("FILE_SERVER_MASTER_KEY") ?? config.MasterApiKey;

try
{
    config.Validate();
}
catch (InvalidOperationException ex)
{
    McpLogger.Error(ex.Message);
    return 1;
}

using var httpClient = new McpHttpClient(config);
var handlers = new FileToolHandlers(httpClient);
var server = new McpServer(handlers);
await using var transport = new StdioTransport();

McpLogger.Info($"{McpServer.ServerName} v{McpServer.ServerVersion} started, backend: {config.FileServerBaseUrl}");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// ---------------------------------------------------------------------------
// 主循环：逐行读 stdin（NDJSON）→ 处理 → 写响应到 stdout。
// stdin EOF 或收到 shutdown/exit/Ctrl+C 时退出。
// ---------------------------------------------------------------------------
while (!cts.IsCancellationRequested && !server.ShutdownRequested)
{
    string? line;
    try
    {
        line = await transport.ReadMessageAsync();
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (line is null)
    {
        break; // stdin 关闭，客户端退出
    }
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var request = JsonRpcRequest.TryParse(line, out var parseError);
    if (request is null)
    {
        if (parseError is not null)
        {
            transport.WriteResponse(JsonRpcResponse.FromError(null, parseError).ToJsonText());
        }
        continue;
    }

    var response = await server.HandleAsync(request);
    if (response is not null)
    {
        transport.WriteResponse(response.ToJsonText());
    }
}

httpClient.Dispose();
McpLogger.Info("MCP server shut down");
return 0;
