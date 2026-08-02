using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FileUploadServer.Tests.Mcp.TestHelpers;

/// <summary>
/// 可编程 HttpMessageHandler：按序列/默认返回配置的响应，记录所有请求 URL 与 body。
/// 用于模拟后端 API 的成功、失败、重试序列与连接异常。
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly List<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _queue = new();
    private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _fallback;
    private readonly object _lock = new();

    /// <summary>所有收到的请求（按时间顺序）。</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>所有请求的 body 文本（当 CaptureBodies=true 时填充，用于校验 multipart 等）。</summary>
    public List<string?> RequestBodies { get; } = new();

    /// <summary>是否捕获请求 body 到 RequestBodies。</summary>
    public bool CaptureBodies { get; set; }

    public int RequestCount => Requests.Count;

    public HttpRequestMessage LastRequest => Requests[^1];

    /// <summary>固定响应：所有请求都返回该 handler 的结果。</summary>
    public void SetDefault(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _fallback = handler;

    public void SetDefault(HttpStatusCode status, string body = "", string contentType = "application/json")
        => _fallback = _ => Task.FromResult(JsonResponse(status, body, contentType));

    /// <summary>固定抛异常（模拟连接失败 / DNS / SSL 错误）。</summary>
    public void SetDefaultException(Func<HttpRequestMessage, Exception> factory) => _fallback = req => throw factory(req);

    /// <summary>按调用顺序返回的响应序列（用于重试测试：503 → 200）。</summary>
    public void SetSequence(params (HttpStatusCode Status, string Body)[] responses)
    {
        foreach (var (status, body) in responses)
        {
            _queue.Add(_ => Task.FromResult(JsonResponse(status, body)));
        }
    }

    public void SetSequence(params HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
        {
            _queue.Add(_ => Task.FromResult(response));
        }
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string body = "", string contentType = "application/json")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    public static HttpResponseMessage BinaryResponse(HttpStatusCode status, byte[] data, string fileName, string contentType)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Requests.Add(request);
        }

        string? body = null;
        if (CaptureBodies && request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        lock (_lock)
        {
            RequestBodies.Add(body);
        }

        Func<HttpRequestMessage, Task<HttpResponseMessage>>? handler;
        lock (_lock)
        {
            handler = _queue.Count > 0 ? _queue[0] : _fallback;
            if (_queue.Count > 0)
            {
                _queue.RemoveAt(0);
            }
        }

        if (handler is null)
        {
            throw new InvalidOperationException($"No mock response configured for {request.Method} {request.RequestUri}");
        }

        // 模拟真实 HttpClient 对慢服务器的取消：handler 忽略令牌时（如 Task.Delay(60s)），
        // 令牌触发仍应让发送抛 TaskCanceledException，而不是等 handler 完成。
        var handlerTask = handler(request);
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(handlerTask, cancellation);
        if (completed != handlerTask)
        {
            throw new TaskCanceledException();
        }

        return await handlerTask;
    }
}
