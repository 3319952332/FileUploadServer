using System.Text.Json.Nodes;

namespace FileUploadServer.Mcp.Server;

/// <summary>
/// MCP 工具定义（tools/list 返回的元素）。
/// </summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        // InputSchema 是静态单例实例，必须深拷贝，否则重复序列化时抛
        // "The node already has a parent"（同一节点被挂到多个父节点）。
        ["inputSchema"] = InputSchema.DeepClone(),
    };
}

/// <summary>
/// 6 个文件管理工具定义。inputSchema 严格遵循 JSON Schema 7。
/// description 必须包含使用场景与注意事项 —— 这是 LLM 决定是否调用的唯一依据。
/// </summary>
public static class ToolDefinitions
{
    private static JsonObject Schema(JsonObject properties, params string[] requiredProps)
    {
        var required = new JsonArray();
        foreach (var prop in requiredProps)
        {
            required.Add(prop);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    public static ToolDefinition FileList { get; } = new()
    {
        Name = "file_list",
        Description = "获取当前 API 密钥可访问的文件列表。Admin 密钥返回所有文件，Temporary 密钥仅返回自己上传的文件。结果按上传时间倒序排列。注意：此操作仅返回文件元数据（名称、大小、类型等），不包含文件内容。",
        InputSchema = Schema(new JsonObject(), []),
    };

    public static ToolDefinition FileInfo { get; } = new()
    {
        Name = "file_info",
        Description = "根据文件 ID 获取单个文件的详细信息（元数据）。返回文件名、大小、MIME 类型、上传时间、存储模式、是否公开等信息。注意：不包含文件内容，如需获取文件内容请使用 file_download。",
        InputSchema = Schema(
            new JsonObject
            {
                ["file_id"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "文件 ID，从 file_list 返回结果中获取",
                },
            },
            "file_id"),
    };

    public static ToolDefinition FileUpload { get; } = new()
    {
        Name = "file_upload",
        Description = "上传一个文件到服务器。支持本地磁盘存储和 WebSocket 远程存储（根据路径前缀自动路由）。如果服务器启用了加密，文件会被 AES-256-GCM 透明加密后存储。最大支持 1GB 单文件。\n\n使用场景：当用户要求保存、存储或上传文件时使用。\n\n注意事项：\n- remote_path 以 / 开头，如 /documents/report.pdf\n- 如果 remote_path 匹配某个 WS 存储节点的路径前缀，文件将转发到该远程节点\n- 上传成功后返回文件元数据，包含可用于后续操作的 file_id",
        InputSchema = Schema(
            new JsonObject
            {
                ["local_file_path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "本地文件的绝对路径，MCP Server 将读取此文件并上传",
                },
                ["remote_path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "服务器上的存储路径，如 /documents/report.pdf。以 / 开头。如果不指定则使用原始文件名存储在根目录。",
                },
            },
            "local_file_path"),
    };

    public static ToolDefinition FileDownload { get; } = new()
    {
        Name = "file_download",
        Description = "根据文件 ID 下载文件内容。服务器会透明解密（如果文件是加密存储的），并以流式方式返回。支持从本地磁盘和 WebSocket 远程存储节点下载。\n\n使用场景：当用户要求获取、查看或下载文件内容时使用。\n\n注意事项：\n- 大文件下载可能需要较长时间（最大 300s 超时）\n- 下载的文件内容以 Base64 编码返回\n- 如果文件存储在离线 WS 节点上，操作将失败并返回 -32005 错误\n- 返回的 mime_type 可用于判断如何处理文件内容",
        InputSchema = Schema(
            new JsonObject
            {
                ["file_id"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "要下载的文件 ID",
                },
            },
            "file_id"),
    };

    public static ToolDefinition FileDelete { get; } = new()
    {
        Name = "file_delete",
        Description = "根据文件 ID 删除文件。此操作会同时删除数据库记录和物理文件（本地磁盘或 WS 远程节点）。\n\n使用场景：当用户要求删除、移除或清理文件时使用。\n\n注意事项：\n- 删除操作不可逆，请确认后再执行\n- 只能删除当前 API 密钥有权访问的文件\n- 如果文件存储在 WS 远程节点且该节点离线，本地数据库记录仍会被删除，但远程文件可能残留\n- 加密文件的子目录结构也会被正确清理",
        InputSchema = Schema(
            new JsonObject
            {
                ["file_id"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "要删除的文件 ID",
                },
            },
            "file_id"),
    };

    public static ToolDefinition FileSetPublic { get; } = new()
    {
        Name = "file_set_public",
        Description = "设置文件的公共访问标记。设为公开后，文件可通过 /p/{public_path} 路径匿名访问（需满足 IP 白名单、限流等条件）。取消公开后，文件只能通过 API Key 访问。\n\n使用场景：需要分享文件给没有 API 密钥的外部用户时使用。\n\n注意事项：\n- 需要 Admin 类型的 API 密钥\n- public_path 是公开访问的唯一路径标识，如 /shared/image.jpg\n- 取消公开时设置 is_public=false 即可\n- 公开文件受 IP 白名单/黑名单、限流、文件大小限制等多重保护",
        InputSchema = Schema(
            new JsonObject
            {
                ["file_id"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "要设置公开访问的文件 ID",
                },
                ["is_public"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "是否公开：true=设为公开，false=取消公开",
                },
                ["public_path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "公开访问路径，如 /shared/report.pdf。仅当 is_public=true 时需要。",
                },
            },
            "file_id", "is_public"),
    };

    // 必须在 6 个工具定义之后声明：C# 静态字段按文本顺序初始化，
    // 若 All 在定义之前，会捕获到尚未初始化的 null。
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        FileList,
        FileInfo,
        FileUpload,
        FileDownload,
        FileDelete,
        FileSetPublic,
    ];
}
