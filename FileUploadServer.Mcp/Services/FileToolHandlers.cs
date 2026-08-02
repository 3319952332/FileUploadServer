using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FileUploadServer.Mcp.Protocol;
using FileUploadServer.Mcp.Server;

namespace FileUploadServer.Mcp.Services;

/// <summary>
/// 6 个文件工具的处理器。每个 handler 先做参数校验（抛 McpError -32602），
/// 再通过 McpHttpClient 调用后端 API。
/// 成功：透传后端响应（或结构化 JSON）；下游失败：ErrorMapper 转为 isError:true。
/// </summary>
public sealed class FileToolHandlers
{
    private readonly McpHttpClient _http;

    public FileToolHandlers(McpHttpClient http)
    {
        _http = http;
    }

    public async Task<CallToolResult> InvokeAsync(string toolName, JsonObject? arguments)
    {
        return toolName switch
        {
            "file_list" => await HandleFileListAsync(),
            "file_info" => await HandleFileInfoAsync(arguments),
            "file_upload" => await HandleFileUploadAsync(arguments),
            "file_download" => await HandleFileDownloadAsync(arguments),
            "file_delete" => await HandleFileDeleteAsync(arguments),
            "file_set_public" => await HandleFileSetPublicAsync(arguments),
            _ => throw new McpError(JsonRpcError.Codes.MethodNotFound, $"Unknown tool: {toolName}"),
        };
    }

    // ---------------------------------------------------------------- file_list
    private async Task<CallToolResult> HandleFileListAsync()
    {
        var response = await _http.SendWithRetryAsync(HttpMethod.Get, "/api/files", null, _http.GetTimeout(false));
        return response.IsSuccessStatusCode
            ? CallToolResult.Success(await response.Content.ReadAsStringAsync())
            : await ErrorMapper.ToErrorResultAsync(response, "file_list");
    }

    // ---------------------------------------------------------------- file_info
    private async Task<CallToolResult> HandleFileInfoAsync(JsonObject? args)
    {
        var fileId = RequireInt(args, "file_id");
        var response = await _http.SendWithRetryAsync(HttpMethod.Get, $"/api/files/{fileId}", null, _http.GetTimeout(false));
        return response.IsSuccessStatusCode
            ? CallToolResult.Success(await response.Content.ReadAsStringAsync())
            : await ErrorMapper.ToErrorResultAsync(response, $"file_info id={fileId}");
    }

    // ---------------------------------------------------------------- file_upload
    private async Task<CallToolResult> HandleFileUploadAsync(JsonObject? args)
    {
        var localPath = RequireString(args, "local_file_path");
        if (!File.Exists(localPath))
        {
            throw new McpError(JsonRpcError.Codes.InvalidParams, $"文件不存在: {localPath}");
        }

        var remotePath = args?["remote_path"]?.GetValue<string>();

        using var fileStream = File.OpenRead(localPath);
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(localPath));
        form.Add(fileContent, "file", Path.GetFileName(localPath));
        if (!string.IsNullOrEmpty(remotePath))
        {
            form.Add(new StringContent(remotePath), "path");
        }

        var response = await _http.SendOnceAsync(HttpMethod.Post, "/api/files", form, _http.GetTimeout(isLargeTransfer: true));
        return response.IsSuccessStatusCode
            ? CallToolResult.Success(await response.Content.ReadAsStringAsync())
            : await ErrorMapper.ToErrorResultAsync(response, $"file_upload {localPath}");
    }

    // ---------------------------------------------------------------- file_download
    private async Task<CallToolResult> HandleFileDownloadAsync(JsonObject? args)
    {
        var fileId = RequireInt(args, "file_id");
        var response = await _http.SendWithRetryAsync(
            HttpMethod.Get, $"/api/files/download/{fileId}", null, _http.GetTimeout(isLargeTransfer: true),
            retryOnTimeout: false);
        if (!response.IsSuccessStatusCode)
        {
            return await ErrorMapper.ToErrorResultAsync(response, $"file_download id={fileId}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName
                       ?? $"file_{fileId}";
        fileName = fileName.Trim('"');
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        var data = new JsonObject
        {
            ["file_id"] = fileId,
            ["file_name"] = fileName,
            ["content_type"] = contentType,
            ["mime_type"] = contentType,
            ["content_base64"] = Convert.ToBase64String(bytes),
        };

        return CallToolResult.Success(data.ToJsonString(McpJson.SerializeOptions));
    }

    // ---------------------------------------------------------------- file_delete
    private async Task<CallToolResult> HandleFileDeleteAsync(JsonObject? args)
    {
        var fileId = RequireInt(args, "file_id");
        var response = await _http.SendWithRetryAsync(HttpMethod.Delete, $"/api/files/{fileId}", null, _http.GetTimeout(false));
        if (response.IsSuccessStatusCode)
        {
            var result = new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonObject
                {
                    ["file_id"] = fileId,
                    ["deleted"] = true,
                },
            };
            return CallToolResult.Success(result.ToJsonString(McpJson.SerializeOptions));
        }

        return await ErrorMapper.ToErrorResultAsync(response, $"file_delete id={fileId}");
    }

    // ---------------------------------------------------------------- file_set_public
    private async Task<CallToolResult> HandleFileSetPublicAsync(JsonObject? args)
    {
        var fileId = RequireInt(args, "file_id");
        var isPublic = RequireBool(args, "is_public");
        string? publicPath = null;
        if (isPublic)
        {
            publicPath = args?["public_path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(publicPath))
            {
                throw new McpError(JsonRpcError.Codes.InvalidParams, "is_public=true 时必须提供 public_path");
            }
        }

        var body = new JsonObject
        {
            ["isPublic"] = isPublic,
            ["publicPath"] = publicPath,
        };
        var content = new StringContent(body.ToJsonString(McpJson.SerializeOptions), Encoding.UTF8, "application/json");

        var response = await _http.SendWithRetryAsync(
            HttpMethod.Put, $"/api/admin/files/{fileId}/public", content, _http.GetTimeout(false));
        return response.IsSuccessStatusCode
            ? CallToolResult.Success(await response.Content.ReadAsStringAsync())
            : await ErrorMapper.ToErrorResultAsync(response, $"file_set_public id={fileId}");
    }

    // ---------------------------------------------------------------- 参数校验
    private static int RequireInt(JsonObject? args, string name)
    {
        if (args is null || !args.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
        {
            throw new McpError(JsonRpcError.Codes.InvalidParams, $"缺少必填参数: {name}");
        }
        if (value.TryGetValue<int>(out var result))
        {
            return result;
        }
        throw new McpError(JsonRpcError.Codes.InvalidParams, $"参数类型错误: {name} 必须是整数");
    }

    private static bool RequireBool(JsonObject? args, string name)
    {
        if (args is null || !args.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
        {
            throw new McpError(JsonRpcError.Codes.InvalidParams, $"缺少必填参数: {name}");
        }
        if (value.TryGetValue<bool>(out var result))
        {
            return result;
        }
        throw new McpError(JsonRpcError.Codes.InvalidParams, $"参数类型错误: {name} 必须是布尔值");
    }

    private static string RequireString(JsonObject? args, string name)
    {
        if (args is null || !args.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
        {
            throw new McpError(JsonRpcError.Codes.InvalidParams, $"缺少必填参数: {name}");
        }
        if (value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }
        throw new McpError(JsonRpcError.Codes.InvalidParams, $"参数类型错误: {name} 必须是字符串");
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".tar" => "application/x-tar",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };
    }
}
