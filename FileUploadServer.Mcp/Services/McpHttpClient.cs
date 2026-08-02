using FileUploadServer.Mcp.Protocol;

namespace FileUploadServer.Mcp.Services;

/// <summary>
/// 封装对后端文件服务器的 HTTP 调用：Master Key 注入、超时控制、指数退避重试。
/// </summary>
public sealed class McpHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly McpServerConfig _config;
    private readonly TimeSpan _retryBaseDelay;

    public McpHttpClient(McpServerConfig config, HttpMessageHandler? handler = null, TimeSpan? retryBaseDelay = null)
    {
        _config = config;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan; // 超时由每次请求的 CancellationTokenSource 精确控制
    }

    /// <summary>大文件传输（上传/下载）用 300s，其他用 30s。</summary>
    public TimeSpan GetTimeout(bool isLargeTransfer) => TimeSpan.FromSeconds(
        isLargeTransfer ? _config.RequestTimeoutSeconds : _config.ShortRequestTimeoutSeconds);

    /// <summary>
    /// 带重试的请求：5xx / 超时 / 连接失败按指数退避重试（最多 MaxRetries 次）。
    /// content 必须可重复发送（StringContent 等内存缓冲内容；上传用 SendOnceAsync）。
    /// </summary>
    public async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method,
        string endpoint,
        HttpContent? content,
        TimeSpan timeout,
        bool retryOnTimeout = true,
        CancellationToken ct = default)
    {
        var maxAttempts = _config.MaxRetries + 1;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var request = new HttpRequestMessage(method, BuildUrl(endpoint)) { Content = content };
                var response = await _httpClient.SendAsync(request, cts.Token);

                // 仅 5xx 重试；4xx 与成功直接返回
                if ((int)response.StatusCode < 500 || attempt == maxAttempts - 1)
                {
                    return response;
                }

                await DelayBeforeRetryAsync(attempt, cts.Token);
            }
            catch (TaskCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                if (!retryOnTimeout || attempt == maxAttempts - 1)
                {
                    throw new McpError(JsonRpcError.Codes.Timeout,
                        $"请求超时 ({timeout.TotalSeconds}s): {method.Method} {endpoint}");
                }
                await DelayBeforeRetryAsync(attempt, CancellationToken.None);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == maxAttempts - 1)
                {
                    throw new McpError(JsonRpcError.Codes.ServiceUnreachable, $"服务不可达: {ex.Message}");
                }
                await DelayBeforeRetryAsync(attempt, CancellationToken.None);
            }
        }

        throw new McpError(JsonRpcError.Codes.ServiceUnreachable, "重试耗尽，服务不可达");
    }

    /// <summary>
    /// 单次请求（不重试）：用于文件上传。multipart 内容含文件流，不可重复发送，
    /// 且重新上传可能造成后端重复存储，故不重试。
    /// </summary>
    public async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string endpoint,
        HttpContent? content,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var request = new HttpRequestMessage(method, BuildUrl(endpoint)) { Content = content };
            return await _httpClient.SendAsync(request, cts.Token);
        }
        catch (TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new McpError(JsonRpcError.Codes.Timeout, $"请求超时 ({timeout.TotalSeconds}s): {method.Method} {endpoint}");
        }
        catch (HttpRequestException ex)
        {
            throw new McpError(JsonRpcError.Codes.ServiceUnreachable, $"服务不可达: {ex.Message}");
        }
    }

    /// <summary>
    /// 拼接带鉴权的 URL：{baseUrl}{endpoint}?key={MasterApiKey}。
    /// 不用 HttpUtility.ParseQueryString（System.Web，.NET Core 不可用）。
    /// </summary>
    private string BuildUrl(string endpoint)
    {
        var baseUrl = _config.FileServerBaseUrl.TrimEnd('/');
        var sep = endpoint.Contains('?') ? "&" : "?";
        return $"{baseUrl}{endpoint}{sep}key={Uri.EscapeDataString(_config.MasterApiKey)}";
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken ct)
    {
        // 指数退避：第 1 次重试前等 base^1，第 2 次等 base^3（默认 1s → 3s）
        var delayMs = _retryBaseDelay.TotalMilliseconds * Math.Pow(3, attempt);
        McpLogger.Warn($"请求失败，等待 {delayMs:0}ms 后重试（attempt {attempt + 1}）");
        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
    }

    public void Dispose() => _httpClient.Dispose();
}
