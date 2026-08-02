using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>LIFE-01 ~ LIFE-05：协议生命周期。</summary>
public class McpLifecycleTests
{
    // ------------------------------------------------------------------ LIFE-01
    [Fact]
    public async Task Initialize_ReturnsCorrectCapabilities()
    {
        using var fake = new FakeMcpServer();
        var response = await fake.InitializeAsync();

        Assert.NotNull(response);
        Assert.Null(response!.Error);

        var result = response.Result!;
        Assert.NotNull(result["serverInfo"]);
        Assert.Equal("file-upload-server-mcp", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(result["serverInfo"]!["version"]!.GetValue<string>()));
        Assert.True(result["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ LIFE-02
    [Fact]
    public async Task Initialize_RejectsUnsupportedProtocolVersion()
    {
        using var fake = new FakeMcpServer();
        var response = await fake.InitializeAsync(protocolVersion: "999.0.0");

        Assert.NotNull(response);
        Assert.NotNull(response!.Error);
        Assert.Equal(JsonRpcError.Codes.InvalidParams, response.Error!.Code);
        Assert.Contains("Unsupported protocol version", response.Error.Message);
    }

    // ------------------------------------------------------------------ LIFE-03
    [Fact]
    public async Task Initialized_ThenToolsList_Works()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();

        var response = await fake.ListToolsAsync();
        Assert.NotNull(response);
        Assert.Null(response!.Error);
        var tools = response.Result!["tools"] as System.Text.Json.Nodes.JsonArray;
        Assert.Equal(6, tools!.Count);
    }

    // ------------------------------------------------------------------ LIFE-04
    [Fact]
    public async Task ToolsList_BeforeInitialized_ReturnsNotInitializedError()
    {
        using var fake = new FakeMcpServer();
        var response = await fake.ListToolsAsync();

        Assert.NotNull(response);
        Assert.Equal(JsonRpcError.Codes.NotInitialized, response!.ErrorCode());
    }

    // ------------------------------------------------------------------ LIFE-05
    [Fact]
    public async Task Shutdown_SetsFlag_AndDispose_ReleasesResources()
    {
        var fake = new FakeMcpServer();
        var request = JsonRpcRequest.TryParse(
            """{"jsonrpc":"2.0","id":9,"method":"shutdown"}""", out _)!;
        var response = await fake.Server.HandleAsync(request);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        Assert.True(fake.Server.ShutdownRequested);

        // 释放 HttpClient 不应抛异常
        fake.Dispose();
    }

    // ------------------------------------------------------------------ 补充：exit 通知触发关闭
    [Fact]
    public async Task ExitNotification_SetsShutdownFlag()
    {
        using var fake = new FakeMcpServer();
        var request = JsonRpcRequest.TryParse(
            """{"jsonrpc":"2.0","method":"exit"}""", out _)!;
        await fake.Server.HandleNotificationAsync(request);

        Assert.True(fake.Server.ShutdownRequested);
    }

    // ------------------------------------------------------------------ 补充：未知方法
    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        using var fake = new FakeMcpServer();
        var request = JsonRpcRequest.TryParse(
            """{"jsonrpc":"2.0","id":5,"method":"bogus_method"}""", out _)!;
        var response = await fake.Server.HandleAsync(request);

        Assert.Equal(JsonRpcError.Codes.MethodNotFound, response!.ErrorCode());
    }
}
