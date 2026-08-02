using System.Net;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>FL-01 ~ FL-03：file_list。</summary>
public class McpFileListTests
{
    private const string FileArrayJson =
        """[{"id":42,"fileName":"report.pdf","fileSize":1024000,"contentType":"application/pdf","uploadedAt":"2026-08-02T10:30:00"}]""";

    // ------------------------------------------------------------------ FL-01
    [Fact]
    public async Task FileList_ReturnsFileArray_OnSuccess()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.OK, FileArrayJson);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.False(response!.ToolIsError());
        Assert.Contains("report.pdf", response.ToolText());
        Assert.Contains("1024000", response.ToolText());
    }

    // ------------------------------------------------------------------ FL-02
    [Fact]
    public async Task FileList_PassesThroughBackendResponse_ForAdmin()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.OK, FileArrayJson);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        // 透传后端响应，不重新包装/过滤
        Assert.Equal(FileArrayJson, response!.ToolText());
    }

    // ------------------------------------------------------------------ FL-03
    [Fact]
    public async Task FileList_Unauthorized_ReturnsPermissionError()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.SetDefault(HttpStatusCode.Unauthorized, "Unauthorized: invalid or expired key");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_list");

        Assert.True(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, parsed["error_code"]!.GetValue<int>());
        Assert.Contains("密钥无效", parsed["message"]!.GetValue<string>());
    }
}
