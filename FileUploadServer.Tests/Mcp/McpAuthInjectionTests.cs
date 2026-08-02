using System.Net;
using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>AUTH-01 ~ AUTH-03：鉴权注入。</summary>
public class McpAuthInjectionTests
{
    // ------------------------------------------------------------------ AUTH-01
    [Fact]
    public async Task MasterKey_IsInjected_IntoAllRequests()
    {
        using var fake = new FakeMcpServer(masterKey: "auth-key-123");
        fake.HttpHandler.SetDefault(HttpStatusCode.OK, "[]");
        await fake.InitializeAndNotifyAsync();

        await fake.CallToolAsync("file_list");

        var query = fake.HttpHandler.LastRequest.RequestUri!.Query;
        Assert.Contains("key=auth-key-123", query);
    }

    // ------------------------------------------------------------------ AUTH-02
    [Fact]
    public void EmptyMasterKey_ThrowsAtStartup()
    {
        var config = new McpServerConfig
        {
            FileServerBaseUrl = "http://localhost:5000",
            MasterApiKey = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("Master API key", ex.Message);
    }

    // ------------------------------------------------------------------ AUTH-03
    [Fact]
    public async Task ExpiredKey_ReturnsPermissionError()
    {
        using var fake = new FakeMcpServer(masterKey: "expired-key");
        fake.HttpHandler.SetDefault(HttpStatusCode.Unauthorized, "Unauthorized: invalid or expired key");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, response.ParseToolText()["error_code"]!.GetValue<int>());
    }
}
