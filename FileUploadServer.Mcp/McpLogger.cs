namespace FileUploadServer.Mcp;

/// <summary>
/// 极简结构化日志。**必须写 stderr** —— stdout 是 MCP 协议通道，任何日志输出到 stdout 都会破坏协议。
/// </summary>
public static class McpLogger
{
    private static readonly object _lock = new();

    /// <summary>可替换的日志写入器（默认 stderr），测试时用于捕获日志。</summary>
    public static Action<string> Writer { get; set; } = msg => Console.Error.WriteLine(msg);

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(Exception ex, string message) => Write("ERROR", $"{message}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        lock (_lock)
        {
            Writer?.Invoke(line);
        }
    }
}
