using System.CommandLine;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.WsClient;
using FileUploadServer.WsClient.Storage;

var rootCommand = new RootCommand("FileUploadServer WS Client - WebSocket 文件存储客户端");

// 命令行选项
var serverOption = new Option<string>(
    "--server",
    description: "WebSocket 服务端地址，如 ws://localhost:5005");

var clientIdOption = new Option<string>(
    "--client-id",
    description: "客户端 ID");

var clientSecretOption = new Option<string>(
    "--client-secret",
    description: "客户端密钥");

var storagePathOption = new Option<string>(
    "--storage-path",
    description: "本地存储路径（仅 Local 模式使用）",
    getDefaultValue: () => "./storage");

var modeOption = new Option<string>(
    "--mode",
    description: "运行模式：ws (WebSocket 远程) 或 local (本地磁盘)",
    getDefaultValue: () => "ws");

var pathsOption = new Option<string[]>(
    "--paths",
    description: "支持的路径前缀（仅 WS 模式使用），如 /public/* /shared/*",
    getDefaultValue: () => Array.Empty<string>());

rootCommand.AddOption(serverOption);
rootCommand.AddOption(clientIdOption);
rootCommand.AddOption(clientSecretOption);
rootCommand.AddOption(storagePathOption);
rootCommand.AddOption(modeOption);
rootCommand.AddOption(pathsOption);

rootCommand.SetHandler(async (context) =>
{
    var server = context.ParseResult.GetValueForOption(serverOption);
    var clientId = context.ParseResult.GetValueForOption(clientIdOption);
    var clientSecret = context.ParseResult.GetValueForOption(clientSecretOption);
    var storagePath = context.ParseResult.GetValueForOption(storagePathOption);
    var mode = context.ParseResult.GetValueForOption(modeOption);
    var paths = context.ParseResult.GetValueForOption(pathsOption);

    // WS 模式必须提供 server, client-id, client-secret
    if (mode == "ws" && (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)))
    {
        Console.Error.WriteLine("Error: WS 模式需要提供 --server, --client-id 和 --client-secret 参数");
        Console.Error.WriteLine("用法: FileUploadServer.WsClient --mode ws --server ws://localhost:5005 --client-id node-1 --client-secret sk-wsc-xxxxx");
        Environment.Exit(1);
    }

    Console.WriteLine($"FileUploadServer WS Client v1.0");
    Console.WriteLine($"Mode: {mode}");
    Console.WriteLine($"Storage path: {storagePath}");
    if (mode == "ws")
    {
        Console.WriteLine($"Server: {server}");
        Console.WriteLine($"Client ID: {clientId}");
        Console.WriteLine($"Paths: {string.Join(", ", paths!)}");
    }

    // 创建客户端
    IFileStorageClient client = mode switch
    {
        "local" => new LocalFileStorageClient(storagePath!),
        "ws" => CreateWsClient(server!, clientId!, clientSecret!, paths!),
        _ => throw new ArgumentException($"Unknown mode: {mode}"),
    };

    if (client is WsFileStorageClient wsClient)
    {
        wsClient.OnDisconnected += (sender, args) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disconnected: {args.Reason}, WillReconnect: {args.WillReconnect}");
        };
    }

    // 连接到服务端
    try
    {
        await client.ConnectAsync(server ?? string.Empty, clientId ?? string.Empty, clientSecret ?? string.Empty);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connected successfully");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connection failed: {ex.Message}");
        if (mode == "local")
        {
            // 本地模式连接失败不算错误
            Console.WriteLine("Local mode: proceeding without server connection");
        }
        else
        {
            Environment.Exit(2);
        }
    }

    // 输出存储路径信息
    Console.WriteLine($"Storage path: {Path.GetFullPath(storagePath!)}");
    Console.WriteLine("Press Ctrl+C to stop.");

    // 等待退出信号
    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (sender, args) =>
    {
        args.Cancel = true;
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Shutting down...");
        tcs.TrySetResult();
    };

    // 也处理 SIGTERM
    AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
    {
        tcs.TrySetResult();
    };

    await tcs.Task;

    // 优雅退出
    try
    {
        await client.DisconnectAsync();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disconnected gracefully");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error during disconnect: {ex.Message}");
    }

    if (client is IAsyncDisposable asyncDisposable)
    {
        await asyncDisposable.DisposeAsync();
    }
});

static WsFileStorageClient CreateWsClient(string serverUrl, string clientId, string clientSecret, string[] paths)
{
    var client = new WsFileStorageClient(serverUrl, clientId, clientSecret);
    if (paths.Length > 0)
    {
        client.SupportedPaths = paths;
    }

    return client;
}

return await rootCommand.InvokeAsync(args);
