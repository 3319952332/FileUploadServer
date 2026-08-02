namespace FileUploadServer.Mcp;

/// <summary>
/// MCP Server 配置。来源：appsettings.json 的 McpServer 节，环境变量
/// FILE_SERVER_BASE_URL / FILE_SERVER_MASTER_KEY 覆盖。
/// </summary>
public sealed class McpServerConfig
{
    public string FileServerBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>Admin 类型 Master API Key（必须配置，否则启动失败）。</summary>
    public string MasterApiKey { get; set; } = "";

    /// <summary>上传/下载等大传输超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>列表/详情/删除等轻量操作超时（秒）。</summary>
    public int ShortRequestTimeoutSeconds { get; set; } = 30;

    /// <summary>5xx 最大重试次数。</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// 启动校验：MasterApiKey 为空时抛 InvalidOperationException（AUTH-02）。
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MasterApiKey))
        {
            throw new InvalidOperationException(
                "Master API key is not configured. Set FILE_SERVER_MASTER_KEY env var or McpServer:MasterApiKey in appsettings.json.");
        }
    }
}
