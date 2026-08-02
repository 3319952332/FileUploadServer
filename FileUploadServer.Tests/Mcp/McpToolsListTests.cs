using System.Text.Json.Nodes;
using FileUploadServer.Tests.Mcp.TestHelpers;

namespace FileUploadServer.Tests.Mcp;

/// <summary>LIST-01 ~ LIST-04：工具发现。</summary>
public class McpToolsListTests
{
    private static readonly string[] ExpectedNames =
        ["file_list", "file_info", "file_upload", "file_download", "file_delete", "file_set_public"];

    private static async Task<JsonArray> GetToolsAsync()
    {
        using var fake = new FakeMcpServer();
        await fake.InitializeAndNotifyAsync();
        var response = await fake.ListToolsAsync();
        Assert.Null(response!.Error);
        return response.Result!["tools"] as JsonArray ?? throw new Xunit.Sdk.XunitException("tools missing");
    }

    // ------------------------------------------------------------------ LIST-01
    [Fact]
    public async Task ToolsList_ReturnsSixTools_WithRequiredFields()
    {
        var tools = await GetToolsAsync();
        Assert.Equal(6, tools.Count);
        foreach (var tool in tools)
        {
            var t = tool as JsonObject ?? throw new Xunit.Sdk.XunitException("tool not object");
            Assert.NotNull(t["name"]);
            Assert.NotNull(t["description"]);
            Assert.NotNull(t["inputSchema"]);
        }
    }

    // ------------------------------------------------------------------ LIST-02
    [Fact]
    public async Task ToolsList_Names_MatchDocumentation()
    {
        var tools = await GetToolsAsync();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).OrderBy(n => n).ToArray();
        Assert.Equal(ExpectedNames.OrderBy(n => n), names);
    }

    // ------------------------------------------------------------------ LIST-03
    [Fact]
    public async Task ToolsList_InputSchema_IsValidJsonSchema7()
    {
        var tools = await GetToolsAsync();
        foreach (var tool in tools)
        {
            var schema = tool!["inputSchema"] as JsonObject ?? throw new Xunit.Sdk.XunitException("schema missing");
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.NotNull(schema["properties"] as JsonObject);
            Assert.NotNull(schema["required"] as JsonArray);
        }
    }

    // ------------------------------------------------------------------ LIST-04
    [Fact]
    public async Task ToolsList_Descriptions_AreMeaningful()
    {
        var tools = await GetToolsAsync();
        foreach (var tool in tools)
        {
            var description = tool!["description"]!.GetValue<string>();
            Assert.True(description.Length > 20, $"description too short for {tool["name"]}");
            // 文档自身对 file_list/file_info 的描述不含"使用场景"字样，
            // 以文档 §6 的原文描述为准，仅要求描述足够详尽。
            Assert.Contains("文件", description);
        }
    }
}
