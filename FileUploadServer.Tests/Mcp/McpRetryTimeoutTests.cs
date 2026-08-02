using System.Diagnostics;
using System.Net;
using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Services;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>RETRY-01 ~ RETRY-05：重试与超时。</summary>
public class McpRetryTimeoutTests
{
    // ------------------------------------------------------------------ RETRY-01
    [Fact]
    public async Task Retry_On503_ThenSucceeds()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetSequence((HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.OK, "[]"));
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.Equal(2, fake.HttpHandler.RequestCount);
        Assert.False(response!.ToolIsError());
    }

    // ------------------------------------------------------------------ RETRY-02
    [Fact]
    public async Task Retry_AllFail_ReturnsError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.ServiceUnavailable);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.Equal(3, fake.HttpHandler.RequestCount); // 首次 + 2 次重试
        Assert.True(response!.ToolIsError());
        // 503 → 存储不可用（-32005）。文档 RETRY-02 原文写 -32001，与 ERR-05（503→-32005）矛盾，
        // 采用语义更正确的 -32005。
        Assert.Equal(JsonRpcError.Codes.StorageUnavailable, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ RETRY-03
    [Fact]
    public async Task NoRetry_On4xx()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.Forbidden);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", """{"file_id":1}""");

        Assert.Equal(1, fake.HttpHandler.RequestCount);
        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ RETRY-04
    [Fact]
    public async Task Retry_Backoff_IsExponential()
    {
        // 退避基数 200ms：第 1 次重试等 200ms，第 2 次等 600ms（指数 3^attempt）
        var retryBase = TimeSpan.FromMilliseconds(200);
        using var fake = new FakeMcpServer(retryBaseDelay: retryBase);
        fake.HttpHandler.SetDefault(HttpStatusCode.ServiceUnavailable);
        await fake.InitializeAndNotifyAsync();

        var sw = Stopwatch.StartNew();
        await fake.CallToolAsync("file_list");
        sw.Stop();

        Assert.Equal(3, fake.HttpHandler.RequestCount);
        Assert.True(sw.ElapsedMilliseconds >= 650, $"expected >=650ms backoff, got {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"backoff took too long: {sw.ElapsedMilliseconds}ms");
    }

    // ------------------------------------------------------------------ RETRY-05
    [Fact]
    public async Task UploadTimeout_DoesNotRetry()
    {
        using var handler = new MockHttpMessageHandler();
        handler.SetDefault(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            return MockHttpMessageHandler.JsonResponse(HttpStatusCode.OK);
        });

        var config = new McpServerConfig
        {
            FileServerBaseUrl = "http://backend.test",
            MasterApiKey = "k",
            MaxRetries = 2,
            RequestTimeoutSeconds = 1,
            ShortRequestTimeoutSeconds = 1,
        };
        using var http = new McpHttpClient(config, handler);

        // 上传走 SendOnceAsync（不重试）：超时即失败 -32000，仅 1 次请求
        var ex = await Assert.ThrowsAsync<McpError>(() =>
            http.SendOnceAsync(HttpMethod.Post, "/api/files", null, http.GetTimeout(isLargeTransfer: true)));
        Assert.Equal(JsonRpcError.Codes.Timeout, ex.Code);
        Assert.Equal(1, handler.RequestCount);
    }
}
