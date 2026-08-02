namespace FileUploadServer.Tests.Mcp.TestHelpers;

/// <summary>
/// 生成临时测试文件。
/// </summary>
public static class TestFileGenerator
{
    /// <summary>创建唯一的临时目录。</summary>
    public static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>在临时目录中创建文件，返回完整路径。</summary>
    public static string CreateTempFile(string content = "hello mcp", string extension = ".txt")
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, "sample" + extension);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>删除文件或目录（忽略不存在）。</summary>
    public static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略清理失败（Windows 文件锁等）
        }
    }
}
