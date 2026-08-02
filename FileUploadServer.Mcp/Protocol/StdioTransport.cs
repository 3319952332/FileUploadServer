namespace FileUploadServer.Mcp.Protocol;

/// <summary>
/// MCP stdio 传输层：newline-delimited JSON 读写。
/// 读 stdin 取消息，写 stdout 发响应。所有日志必须走 stderr，不能污染 stdout。
/// </summary>
public sealed class StdioTransport : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private bool _disposed;

    public StdioTransport(TextReader? reader = null, TextWriter? writer = null)
    {
        _reader = reader ?? Console.In;
        _writer = writer ?? Console.Out;
    }

    /// <summary>读取一行原始 JSON（trim 空白），流结束返回 null。</summary>
    public async Task<string?> ReadMessageAsync()
    {
        var line = await _reader.ReadLineAsync();
        return line?.Trim();
    }

    /// <summary>将 JSON-RPC 响应写入 stdout 并立即 Flush（保证客户端实时收到）。</summary>
    public void WriteResponse(string jsonText)
    {
        if (_disposed) return;
        _writer.WriteLine(jsonText);
        _writer.Flush();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
