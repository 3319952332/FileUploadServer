using System.Text.Json.Nodes;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Services;

namespace FileUploadServer.Mcp.Server;

/// <summary>
/// MCP Server 核心：生命周期状态机 + JSON-RPC 方法分发。
/// 纯逻辑，不依赖真实 stdio，测试可直接调用 HandleAsync / HandleNotificationAsync。
/// </summary>
public sealed class McpServer
{
    public const string ServerName = "file-upload-server-mcp";
    public const string ServerVersion = "1.0.0";

    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "0.1.0", "1.0",
        "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-03-26",
    };

    private readonly FileToolHandlers _handlers;
    private int _initialized;

    public McpServer(FileToolHandlers handlers)
    {
        _handlers = handlers;
    }

    public bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    /// <summary>收到 shutdown 方法或 exit 通知后为 true，Program 据此退出。</summary>
    public bool ShutdownRequested { get; private set; }

    /// <summary>
    /// 处理一条请求或通知。通知返回 null（无需响应）。
    /// </summary>
    public async Task<JsonRpcResponse?> HandleAsync(JsonRpcRequest request)
    {
        if (request.IsNotification)
        {
            await HandleNotificationAsync(request);
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = request.Method switch
            {
                "initialize" => HandleInitialize(request.Id, request.Params),
                "ping" => JsonRpcResponse.FromResult(request.Id, new JsonObject()),
                "tools/list" => HandleToolsList(request.Id),
                "tools/call" => await HandleToolsCallAsync(request.Id, request.Params),
                "shutdown" => HandleShutdown(request.Id),
                "resources/list" => JsonRpcResponse.FromResult(request.Id, new JsonObject { ["resources"] = new JsonArray() }),
                _ => JsonRpcResponse.FromError(request.Id,
                    new JsonRpcError(JsonRpcError.Codes.MethodNotFound, $"Method not found: {request.Method}")),
            };

            McpLogger.Info($"[{request.Method}] ok in {stopwatch.ElapsedMilliseconds}ms");
            return response;
        }
        catch (McpError ex)
        {
            McpLogger.Warn($"[{request.Method}] error {ex.Code} in {stopwatch.ElapsedMilliseconds}ms: {ex.Message}");
            return JsonRpcResponse.FromError(request.Id, ex.ToJsonRpcError());
        }
        catch (Exception ex)
        {
            McpLogger.Error(ex, $"[{request.Method}] unhandled error");
            return JsonRpcResponse.FromError(request.Id,
                new JsonRpcError(JsonRpcError.Codes.InternalError, $"Internal error: {ex.Message}"));
        }
    }

    /// <summary>处理通知（无需响应）。</summary>
    public Task HandleNotificationAsync(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "notifications/initialized":
                Interlocked.Exchange(ref _initialized, 1);
                McpLogger.Info("Client initialized; tools now available");
                break;
            case "exit":
                ShutdownRequested = true;
                McpLogger.Info("Client sent exit notification");
                break;
            case "notifications/cancelled":
            case "logging/setLevel":
                break;
            default:
                // 未知通知：忽略（JSON-RPC 规范）
                break;
        }

        return Task.CompletedTask;
    }

    private JsonRpcResponse HandleInitialize(long? id, JsonNode? prms)
    {
        var protocolVersion = prms?["protocolVersion"]?.GetValue<string>();
        if (string.IsNullOrEmpty(protocolVersion) || !SupportedProtocolVersions.Contains(protocolVersion))
        {
            return JsonRpcResponse.FromError(id, new JsonRpcError(
                JsonRpcError.Codes.InvalidParams,
                $"Unsupported protocol version: {protocolVersion ?? "<missing>"}. Supported: {string.Join(", ", SupportedProtocolVersions)}"));
        }

        var result = new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
        return JsonRpcResponse.FromResult(id, result);
    }

    private JsonRpcResponse HandleToolsList(long? id)
    {
        if (!IsInitialized)
        {
            return NotInitializedError(id);
        }

        var tools = new JsonArray();
        foreach (var tool in ToolDefinitions.All)
        {
            tools.Add(tool.ToJson());
        }

        return JsonRpcResponse.FromResult(id, new JsonObject { ["tools"] = tools });
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(long? id, JsonNode? prms)
    {
        if (!IsInitialized)
        {
            return NotInitializedError(id);
        }

        var name = prms?["name"]?.GetValue<string>();
        var arguments = prms?["arguments"] as JsonObject;
        if (string.IsNullOrEmpty(name))
        {
            throw new McpError(JsonRpcError.Codes.InvalidParams, "Missing required parameter: name");
        }

        var result = await _handlers.InvokeAsync(name, arguments);
        return JsonRpcResponse.FromResult(id, result.ToJson());
    }

    private JsonRpcResponse HandleShutdown(long? id)
    {
        ShutdownRequested = true;
        McpLogger.Info("Client requested shutdown");
        return JsonRpcResponse.FromResult(id, new JsonObject());
    }

    private static JsonRpcResponse NotInitializedError(long? id) => JsonRpcResponse.FromError(id, new JsonRpcError(
        JsonRpcError.Codes.NotInitialized,
        "Server not initialized: client must send initialize then notifications/initialized"));
}
