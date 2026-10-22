using FileUploadServer.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;

namespace FileUploadServer.Tests.Web;

/// <summary>
/// 鉴权中间件加固：凭据改用请求头、鉴权前不解析请求体、解析前限制请求体大小。
/// 对应 CVE-2026-70181（multipart 缓冲导致内存耗尽）的暴露面收敛。
/// </summary>
public class ApiKeyAuthMiddlewareTests
{
    private const long OneGb = 1073741824L;

    private static ApiKeyAuthMiddleware CreateMiddleware(
        long maxBodySize = OneGb,
        RequestDelegate? next = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Upload:MaxRequestBodySize"] = maxBodySize.ToString(),
            })
            .Build();

        return new ApiKeyAuthMiddleware(next ?? (_ => Task.CompletedTask), configuration);
    }

    // ------------------------------------------------------------ 凭据来源

    [Fact]
    public void ResolveCredential_PrefersHeaderOverQuery()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = "header-key";
        context.Request.QueryString = new QueryString("?key=query-key");

        Assert.Equal("header-key", ApiKeyAuthMiddleware.ResolveCredential(context));
    }

    [Fact]
    public void ResolveCredential_FallsBackToQueryParameter()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?key=query-key");

        Assert.Equal("query-key", ApiKeyAuthMiddleware.ResolveCredential(context));
    }

    [Fact]
    public void ResolveCredential_ReturnsNull_WhenNoCredential()
    {
        var context = new DefaultHttpContext();

        Assert.Null(ApiKeyAuthMiddleware.ResolveCredential(context));
    }

    // ------------------------------------------------- 鉴权前不解析请求体

    [Fact]
    public async Task InvokeAsync_MissingCredential_DoesNotParseRequestBody()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=xyz";
        context.Features.Set<IFormFeature>(new ThrowingFormFeature());

        var nextCalled = false;
        var middleware = CreateMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // dbContext 传 null：未携带凭据时必须在查库之前返回，证明请求体未被解析
        await middleware.InvokeAsync(context, null!);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_FormCredential_IsNotAccepted()
    {
        // 旧的实现会从表单里读 key；现在表单不再参与鉴权
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=xyz";
        context.Features.Set<IFormFeature>(new ThrowingFormFeature());

        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context, null!);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // --------------------------------------------------- 解析前限制请求体

    [Fact]
    public async Task InvokeAsync_OversizedContentLength_Returns413_WithoutCallingNext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentLength = OneGb + 1;

        var nextCalled = false;
        var middleware = CreateMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, null!);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_BodyWithinLimit_PassesSizeGate()
    {
        // 未超限但未携带凭据：应走到鉴权分支（401），而不是被体积闸门拦下
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentLength = 1024;

        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context, null!);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>一旦被读取就抛异常的表单特性：用于证明鉴权阶段没有触碰请求体。</summary>
#pragma warning disable CS8767 // 接口属性的可空性标注与实现不一致，属测试替身特性
    private sealed class ThrowingFormFeature : IFormFeature
    {
        public bool HasFormContentType => true;

        public IFormCollection Form
        {
            get => throw new InvalidOperationException("鉴权阶段不应读取请求体");
            set => throw new InvalidOperationException("鉴权阶段不应读取请求体");
        }

        public IFormCollection ReadForm() => throw new InvalidOperationException("鉴权阶段不应读取请求体");

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("鉴权阶段不应读取请求体");
    }
#pragma warning restore CS8767
}
