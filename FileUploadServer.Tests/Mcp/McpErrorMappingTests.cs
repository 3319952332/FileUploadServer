using System.Net;
using System.Net.Http.Headers;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>ERR-01 ~ ERR-06：HTTP 状态码 → JSON-RPC 错误码映射。</summary>
public class McpErrorMappingTests
{
    // ------------------------------------------------------------------ ERR-01
    [Fact]
    public async Task Http400_MapsTo_InvalidParams()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.BadRequest, "Bad request");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", """{"file_id":1}""");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.InvalidParams, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ ERR-02
    [Fact]
    public async Task Http401_MapsTo_PermissionDenied()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.Unauthorized, "Unauthorized: invalid or expired key");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", """{"file_id":1}""");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ ERR-03
    [Fact]
    public async Task Http403_MapsTo_PermissionDenied()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.Forbidden);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_delete", """{"file_id":1}""");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ ERR-04
    [Fact]
    public async Task Http429_MapsTo_RateLimited_WithRetryAfter()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(_ =>
        {
            var response = MockHttpMessageHandler.JsonResponse(HttpStatusCode.TooManyRequests, "Too Many Requests");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        });
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.True(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(JsonRpcError.Codes.RateLimited, parsed["error_code"]!.GetValue<int>());
        Assert.Equal(45, parsed["data"]!["retry_after_seconds"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ ERR-05
    [Fact]
    public async Task Http503_MapsTo_StorageUnavailable()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.ServiceUnavailable, "Storage client is currently offline");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_download", """{"file_id":5}""");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.StorageUnavailable, response.ParseToolText()["error_code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ ERR-06
    [Fact]
    public async Task ConnectionRefused_MapsTo_ServiceUnreachable()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefaultException(_ => new HttpRequestException("Connection refused"));
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.NotNull(response!.Error);
        Assert.Equal(JsonRpcError.Codes.ServiceUnreachable, response.Error!.Code);
        Assert.Contains("服务不可达", response.Error.Message);
    }
}
