using System.Net;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>FI-01 ~ FI-04：file_info。</summary>
public class McpFileInfoTests
{
    private const string FileJson =
        """{"id":1,"fileName":"a.pdf","fileSize":2048,"contentType":"application/pdf","storageMode":"Local","isPublic":false}""";

    // ------------------------------------------------------------------ FI-01
    [Fact]
    public async Task FileInfo_ReturnsMetadata_OnSuccess()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.OK, FileJson);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", """{"file_id":1}""");

        Assert.False(response!.ToolIsError());
        Assert.Contains("a.pdf", response.ToolText());
        Assert.Contains("2048", response.ToolText());
    }

    // ------------------------------------------------------------------ FI-02
    [Fact]
    public async Task FileInfo_NotFound_ReturnsError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.NotFound);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", """{"file_id":99999}""");

        Assert.True(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(JsonRpcError.Codes.InvalidParams, parsed["error_code"]!.GetValue<int>());
        Assert.Contains("文件不存在", parsed["message"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ FI-03
    [Fact]
    public async Task FileInfo_MissingParam_ReturnsInvalidParams()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", "{}");

        Assert.Equal(JsonRpcError.Codes.InvalidParams, response!.ErrorCode());
        Assert.Contains("缺少必填参数: file_id", response.ErrorMessage());
        Assert.Equal(0, fake.HttpHandler.RequestCount); // 未发送请求
    }

    // ------------------------------------------------------------------ FI-04
    [Fact]
    public async Task FileInfo_WrongType_ReturnsInvalidParams()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_info", """{"file_id":"abc"}""");

        Assert.Equal(JsonRpcError.Codes.InvalidParams, response!.ErrorCode());
        Assert.Contains("参数类型错误", response.ErrorMessage());
        Assert.Equal(0, fake.HttpHandler.RequestCount); // 未发送请求
    }
}
