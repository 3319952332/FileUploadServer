using System.Net;
using System.Text;
using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Services;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>DL-01 ~ DL-04：file_download。</summary>
public class McpFileDownloadTests
{
    // ------------------------------------------------------------------ DL-01
    [Fact]
    public async Task FileDownload_ReturnsBase64Content_OnSuccess()
    {
        var payload = Encoding.UTF8.GetBytes("hello mcp download");
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(_ => Task.FromResult(
            MockHttpMessageHandler.BinaryResponse(HttpStatusCode.OK, payload, "report.pdf", "application/pdf")));
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_download", """{"file_id":1}""");

        Assert.False(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(Convert.ToBase64String(payload), parsed["content_base64"]!.GetValue<string>());
        Assert.Equal("report.pdf", parsed["file_name"]!.GetValue<string>());
        Assert.Equal("application/pdf", parsed["content_type"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ DL-02
    [Fact]
    public async Task FileDownload_NotFound_ReturnsError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.NotFound);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_download", """{"file_id":99999}""");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.InvalidParams, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ DL-03
    [Fact]
    public async Task FileDownload_StorageOffline_ReturnsStorageError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.ServiceUnavailable, "Storage client is currently offline");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_download", """{"file_id":5}""");

        Assert.True(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(JsonRpcError.Codes.StorageUnavailable, parsed["error_code"]!.GetValue<int>());
        Assert.True(parsed["data"]!["retryable"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ DL-04
    [Fact]
    public async Task FileDownload_Timeout_ReturnsTimeoutError_WithoutRetry()
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
            RequestTimeoutSeconds = 1,      // 上传/下载超时 1s（加速测试）
            ShortRequestTimeoutSeconds = 1,
        };
        using var http = new McpHttpClient(config, handler);

        // 下载路径 retryOnTimeout=false：超时即失败，仅 1 次请求
        var ex = await Assert.ThrowsAsync<McpError>(() =>
            http.SendWithRetryAsync(HttpMethod.Get, "/api/files/download/1", null,
                http.GetTimeout(isLargeTransfer: true), retryOnTimeout: false));
        Assert.Equal(JsonRpcError.Codes.Timeout, ex.Code);
        Assert.Equal(1, handler.RequestCount);
    }
}
