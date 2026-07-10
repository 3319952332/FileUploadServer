using System.Text;
using System.Text.RegularExpressions;

namespace FileUploadServer.Core.Services;

/// <summary>
/// 路径 Glob 模式匹配器
/// <para>支持 *（单段通配）和 **（多段通配）两种通配符</para>
/// <para>* 匹配除路径分隔符外的任意字符，** 匹配任意字符包含路径分隔符</para>
/// </summary>
public class PathMatcher
{
    /// <summary>
    /// 路径最大允许长度
    /// </summary>
    private const int MaxPathLength = 2048;

    /// <summary>
    /// 路径分隔符数组，用于安全检查和规范化
    /// </summary>
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// 判断指定路径是否匹配给定的 Glob 模式
    /// </summary>
    /// <param name="path">待匹配的路径</param>
    /// <param name="pattern">Glob 模式</param>
    /// <returns>是否匹配</returns>
    public bool IsMatch(string path, string pattern)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pattern))
            return false;

        // 安全检查：超长路径
        if (path.Length > MaxPathLength)
            return false;

        // 安全检查：拒绝包含 .. 的路径，防止路径遍历攻击
        if (ContainsPathTraversal(path))
            return false;

        var normalizedPath = NormalizePath(path);
        var normalizedPattern = NormalizePattern(pattern);
        var regexPattern = ConvertGlobToRegex(normalizedPattern);

        return Regex.IsMatch(normalizedPath, regexPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    /// <summary>
    /// 判断路径是否匹配模式列表中的任意一个
    /// </summary>
    /// <param name="path">待匹配的路径</param>
    /// <param name="patterns">Glob 模式列表</param>
    /// <returns>是否匹配任意模式</returns>
    public bool MatchesAnyPattern(string path, string[] patterns)
    {
        if (patterns == null || patterns.Length == 0)
            return false;

        for (var i = 0; i < patterns.Length; i++)
        {
            if (IsMatch(path, patterns[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 检查路径是否包含路径遍历攻击特征（.. 段）
    /// </summary>
    private static bool ContainsPathTraversal(string path)
    {
        // 使用分段检查来避免对 /.../ 之类路径的误判
        var segments = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == ".")
                return true;
            if (segment == "..")
                return true;
        }

        // 额外检查路径是否以 .. 开头或包含 /../ 模式
        if (path.Contains("..", StringComparison.Ordinal))
        {
            // 但排除合法的情况如 /.../ （省略号）
            if (!path.Contains("/...", StringComparison.Ordinal) &&
                !path.Contains("\\...", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 规范化路径：统一分隔符、移除前导斜杠
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // 统一路径分隔符为 /
        path = path.Replace('\\', '/');

        // 移除前导斜杠，使匹配更一致
        path = path.TrimStart('/');

        // 移除尾部斜杠
        path = path.TrimEnd('/');

        return path;
    }

    /// <summary>
    /// 规范化模式：统一分隔符、移除前导斜杠
    /// </summary>
    private static string NormalizePattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        // 统一路径分隔符为 /
        pattern = pattern.Replace('\\', '/');

        // 移除前导斜杠
        pattern = pattern.TrimStart('/');

        return pattern;
    }

    /// <summary>
    /// 将 Glob 模式转换为正则表达式
    /// </summary>
    /// <param name="pattern">已规范化的 Glob 模式</param>
    /// <returns>正则表达式字符串</returns>
    private static string ConvertGlobToRegex(string pattern)
    {
        var regex = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // ** 多段通配符：匹配任意字符（包含路径分隔符）
                regex.Append(".*");
                i++; // 跳过第二个 *

                // 跳过 ** 后面的路径分隔符，使得 public/** 匹配 public/ 下的所有内容
                if (i + 1 < pattern.Length && pattern[i + 1] is '/' or '\\')
                {
                    i++;
                }
            }
            else if (c == '*')
            {
                // * 单段通配符：匹配除路径分隔符外的任意字符
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                // ? 单字符通配符：匹配一个非路径分隔符的字符
                regex.Append("[^/]");
            }
            else if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
            {
                // 普通字符：直接添加
                regex.Append(c);
            }
            else
            {
                // 正则表达式特殊字符：转义
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        regex.Append('$');
        return regex.ToString();
    }
}
