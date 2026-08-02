using System.Net;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>UP-01 ~ UP-05：file_upload。</summary>
public class McpFileUploadTests
{
    private const string UploadedJson =
        """{"id":42,"fileName":"test.txt","fileSize":9,"contentType":"text/plain","storageMode":"Local"}""";

    // ------------------------------------------------------------------ UP-01
    [Fact]
    public async Task FileUpload_ReturnsMetadata_OnSuccess()
    {
        var localPath = TestFileGenerator.CreateTempFile("hello mcp", ".txt");
        try
        {
            using var fake = new FakeMcpServer();
            fake.HttpHandler.SetDefault(HttpStatusCode.Created, UploadedJson);
            await fake.InitializeAndNotifyAsync();

            var response = await fake.CallToolAsync("file_upload", $"{{\"local_file_path\":\"{localPath}\"}}");

            Assert.False(response!.ToolIsError());
            Assert.Contains("42", response.ToolText());
            Assert.Contains("test.txt", response.ToolText());
        }
        finally
        {
            TestFileGenerator.Cleanup(localPath);
        }
    }

    // ------------------------------------------------------------------ UP-02
    [Fact]
    public async Task FileUpload_LocalFileMissing_ReturnsInvalidParams()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_upload", """{"local_file_path":"/tmp/notexist.pdf"}""");

        Assert.Equal(JsonRpcError.Codes.InvalidParams, response!.ErrorCode());
        Assert.Contains("文件不存在", response.ErrorMessage());
        Assert.Equal(0, fake.HttpHandler.RequestCount);
    }

    // ------------------------------------------------------------------ UP-03
    [Fact]
    public async Task FileUpload_MissingParam_ReturnsInvalidParams()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();

        var response = await fake.CallToolAsync("file_upload", "{}");

        Assert.Equal(JsonRpcError.Codes.InvalidParams, response!.ErrorCode());
        Assert.Contains("缺少必填参数: local_file_path", response.ErrorMessage());
    }

    // ------------------------------------------------------------------ UP-04
    [Fact]
    public async Task FileUpload_WithRemotePath_SendsPathFormField()
    {
        var localPath = TestFileGenerator.CreateTempFile("hello mcp", ".txt");
        try
        {
            using var fake = new FakeMcpServer();
            fake.HttpHandler.CaptureBodies = true;
            fake.HttpHandler.SetDefault(HttpStatusCode.Created, UploadedJson);
            await fake.InitializeAndNotifyAsync();

            await fake.CallToolAsync("file_upload",
                $"{{\"local_file_path\":\"{localPath}\",\"remote_path\":\"/docs/report.pdf\"}}");

            var body = fake.HttpHandler.RequestBodies[0];
            Assert.NotNull(body);
            // .NET 的 multipart 序列化中 name 不带引号：Content-Disposition: form-data; name=path
            Assert.Contains("name=path", body!);
            Assert.Contains("/docs/report.pdf", body);
            Assert.Contains("name=file", body);
        }
        finally
        {
            TestFileGenerator.Cleanup(localPath);
        }
    }

    // ------------------------------------------------------------------ UP-05
    [Fact]
    public async Task FileUpload_TooLarge_ReturnsError()
    {
        var localPath = TestFileGenerator.CreateTempFile(new string('x', 1024), ".bin");
        try
        {
            using var fake = new FakeMcpServer();
            fake.HttpHandler.SetDefault(HttpStatusCode.RequestEntityTooLarge, "File too large");
            await fake.InitializeAndNotifyAsync();

            var response = await fake.CallToolAsync("file_upload", $"{{\"local_file_path\":\"{localPath}\"}}");

            Assert.True(response!.ToolIsError());
            var parsed = response.ParseToolText();
            Assert.Equal(JsonRpcError.Codes.InvalidParams, parsed["error_code"]!.GetValue<int>());
            Assert.Contains("文件过大", parsed["message"]!.GetValue<string>());
        }
        finally
        {
            TestFileGenerator.Cleanup(localPath);
        }
    }
}
