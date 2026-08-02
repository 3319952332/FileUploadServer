using System.Net;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>DEL-01 ~ DEL-03：file_delete。</summary>
public class McpFileDeleteTests
{
    // ------------------------------------------------------------------ DEL-01
    [Fact]
    public async Task FileDelete_ReturnsSuccess_OnNoContent()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.NoContent);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_delete", """{"file_id":1}""");

        Assert.False(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.True(parsed["data"]!["deleted"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------ DEL-02
    [Fact]
    public async Task FileDelete_Forbidden_ReturnsPermissionError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.Forbidden);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_delete", """{"file_id":99}""");

        Assert.True(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, parsed["error_code"]!.GetValue<int>());
        Assert.Contains("权限不足", parsed["message"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ DEL-03
    [Fact]
    public async Task FileDelete_NotFound_ReturnsError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.NotFound);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_delete", """{"file_id":99999}""");

        Assert.True(response!.ToolIsError());
        Assert.Equal(JsonRpcError.Codes.InvalidParams, response.ParseToolText()["error_code"]!.GetValue<int>());
    }
}
