using FileUploadServer.Mcp;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Server;
using FileUploadServer.Mcp.Services;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>
/// E2E-01 ~ E2E-03：端到端测试，需要真实后端。
/// 未配置 FILE_SERVER_BASE_URL / FILE_SERVER_MASTER_KEY 环境变量时自动跳过。
/// </summary>
public class McpEndToEndTests
{
    private static readonly bool HasBackend =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FILE_SERVER_BASE_URL")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FILE_SERVER_MASTER_KEY"));

    private static McpServer BuildServer()
    {
        var config = new McpServerConfig
        {
            FileServerBaseUrl = Environment.GetEnvironmentVariable("FILE_SERVER_BASE_URL")!,
            MasterApiKey = Environment.GetEnvironmentVariable("FILE_SERVER_MASTER_KEY")!,
        };
        var http = new McpHttpClient(config);
        return new McpServer(new FileToolHandlers(http));
    }

    private static JsonRpcRequest Req(long? id, string method, string paramsJson = "{}")
    {
        var idPart = id.HasValue ? $"\"id\":{id.Value}," : string.Empty;
        return JsonRpcRequest.TryParse($"{{\"jsonrpc\":\"2.0\",{idPart}\"method\":\"{method}\",\"params\":{paramsJson}}}", out _)!;
    }

    // ------------------------------------------------------------------ E2E-01
    [Fact]
    public async Task E2E_UploadDownloadDeleteFlow()
    {
        if (!HasBackend) return;

        var server = BuildServer();
        await server.HandleAsync(Req(1, "initialize", """{"protocolVersion":"2025-03-26"}"""));
        await server.HandleAsync(Req(null, "notifications/initialized"));

        // 上传
        var tmp = Path.Combine(Path.GetTempPath(), $"mcp_e2e_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, "e2e upload download delete flow");
        var up = await server.HandleAsync(Req(2, "tools/call",
            $"{{\"name\":\"file_upload\",\"arguments\":{{\"local_file_path\":\"{tmp}\"}}}}"));
        File.Delete(tmp);

        Assert.NotNull(up);
        Assert.Null(up!.Error);
        var uploaded = System.Text.Json.Nodes.JsonNode.Parse(up.ToolText())!;
        var fileId = uploaded["id"]!.GetValue<int>();
        Assert.True(fileId > 0);

        // 下载并验证内容一致
        var dl = await server.HandleAsync(Req(3, "tools/call",
            $"{{\"name\":\"file_download\",\"arguments\":{{\"file_id\":{fileId}}}}}"));
        Assert.NotNull(dl);
        Assert.False(dl!.ToolIsError());
        var dlData = System.Text.Json.Nodes.JsonNode.Parse(dl.ToolText())!;
        var content = Convert.FromBase64String(dlData["content_base64"]!.GetValue<string>());
        Assert.Equal("e2e upload download delete flow", System.Text.Encoding.UTF8.GetString(content));

        // 删除
        var del = await server.HandleAsync(Req(4, "tools/call",
            $"{{\"name\":\"file_delete\",\"arguments\":{{\"file_id\":{fileId}}}}}"));
        Assert.NotNull(del);
        Assert.False(del!.ToolIsError());

        // 删除后再查 info 应为 404
        var info = await server.HandleAsync(Req(5, "tools/call",
            $"{{\"name\":\"file_info\",\"arguments\":{{\"file_id\":{fileId}}}}}"));
        Assert.NotNull(info);
        Assert.True(info!.ToolIsError());
    }

    // ------------------------------------------------------------------ E2E-02
    [Fact]
    public async Task E2E_AdminKey_CanListAll()
    {
        if (!HasBackend) return;

        var server = BuildServer();
        await server.HandleAsync(Req(1, "initialize", """{"protocolVersion":"2025-03-26"}"""));
        await server.HandleAsync(Req(null, "notifications/initialized"));

        var list = await server.HandleAsync(Req(2, "tools/call",
            """{"name":"file_list","arguments":{}}"""));
        Assert.NotNull(list);
        Assert.False(list!.ToolIsError());
    }

    // ------------------------------------------------------------------ E2E-03
    [Fact]
    public async Task E2E_PublicFile_SetAndUnset()
    {
        if (!HasBackend) return;

        var server = BuildServer();
        await server.HandleAsync(Req(1, "initialize", """{"protocolVersion":"2025-03-26"}"""));
        await server.HandleAsync(Req(null, "notifications/initialized"));

        var tmp = Path.Combine(Path.GetTempPath(), $"mcp_e2e_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, "public file test");
        var up = await server.HandleAsync(Req(2, "tools/call",
            $"{{\"name\":\"file_upload\",\"arguments\":{{\"local_file_path\":\"{tmp}\"}}}}"));
        File.Delete(tmp);
        var fileId = System.Text.Json.Nodes.JsonNode.Parse(up!.ToolText())!["id"]!.GetValue<int>();

        var setPublic = await server.HandleAsync(Req(3, "tools/call",
            $"{{\"name\":\"file_set_public\",\"arguments\":{{\"file_id\":{fileId},\"is_public\":true,\"public_path\":\"/shared/e2e_test.txt\"}}}}"));
        Assert.False(setPublic!.ToolIsError());

        // 清理：取消公开并删除
        await server.HandleAsync(Req(4, "tools/call",
            $"{{\"name\":\"file_set_public\",\"arguments\":{{\"file_id\":{fileId},\"is_public\":false}}}}"));
        await server.HandleAsync(Req(5, "tools/call",
            $"{{\"name\":\"file_delete\",\"arguments\":{{\"file_id\":{fileId}}}}}"));
    }
}
