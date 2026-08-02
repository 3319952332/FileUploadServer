using System.Net;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>PUB-01 ~ PUB-04：file_set_public。</summary>
public class McpFileSetPublicTests
{
    private const string UpdatedJson =
        """{"id":1,"fileName":"doc.pdf","isPublic":true,"publicPath":"/shared/doc.pdf"}""";

    // ------------------------------------------------------------------ PUB-01
    [Fact]
    public async Task FileSetPublic_ToPublic_SendsBody_AndSucceeds()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.CaptureBodies = true;
        fake.HttpHandler.SetDefault(HttpStatusCode.OK, UpdatedJson);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_set_public",
            """{"file_id":1,"is_public":true,"public_path":"/shared/doc.pdf"}""");

        Assert.False(response!.ToolIsError());

        var body = fake.HttpHandler.RequestBodies[0]!;
        Assert.Contains("isPublic", body);
        Assert.Contains("true", body);
        Assert.Contains("/shared/doc.pdf", body);

        var url = fake.HttpHandler.LastRequest.RequestUri!.AbsolutePath;
        Assert.Equal("/api/admin/files/1/public", url);
    }

    // ------------------------------------------------------------------ PUB-02
    [Fact]
    public async Task FileSetPublic_ToPrivate_SendsIsPublicFalse()
    {
        using var fake = new FakeMcpServer();
        fake.HttpHandler.CaptureBodies = true;
        fake.HttpHandler.SetDefault(HttpStatusCode.OK,
            """{"id":1,"fileName":"doc.pdf","isPublic":false,"publicPath":null}""");
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_set_public", """{"file_id":1,"is_public":false}""");

        Assert.False(response!.ToolIsError());
        var body = fake.HttpHandler.RequestBodies[0]!;
        Assert.Contains("\"isPublic\":false", body);
    }

    // ------------------------------------------------------------------ PUB-03
    [Fact]
    public async Task FileSetPublic_PublicWithoutPath_ReturnsInvalidParams()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_set_public", """{"file_id":1,"is_public":true}""");

        Assert.Equal(JsonRpcError.Codes.InvalidParams, response!.ErrorCode());
        Assert.Contains("is_public=true 时必须提供 public_path", response.ErrorMessage());
        Assert.Equal(0, fake.HttpHandler.RequestCount);
    }

    // ------------------------------------------------------------------ PUB-04
    [Fact]
    public async Task FileSetPublic_WithTemporaryKey_ReturnsPermissionError()
    {
        // Temporary 密钥操作的场景：后端对非 Admin 密钥返回 403
        using var fake = new FakeMcpServer(masterKey: "temp-key-not-admin");
        fake.HttpHandler.SetDefault(HttpStatusCode.Forbidden);
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_set_public",
            """{"file_id":1,"is_public":true,"public_path":"/shared/doc.pdf"}""");

        Assert.True(response!.ToolIsError());
        var parsed = response.ParseToolText();
        Assert.Equal(JsonRpcError.Codes.PermissionDenied, parsed["error_code"]!.GetValue<int>());
        Assert.Contains("权限不足", parsed["message"]!.GetValue<string>());
    }
}
