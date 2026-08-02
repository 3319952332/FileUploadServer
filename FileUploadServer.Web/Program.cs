using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Encryption;
using FileUploadServer.Infrastructure.Repositories;
using FileUploadServer.Infrastructure.Services;
using FileUploadServer.Web.Commands;
using FileUploadServer.Web.MessageHandlers;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;
using FileUploadServer.Core.Models;
using FileUploadServer.Core.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

// 设置时区为北京时间
TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
Environment.SetEnvironmentVariable("TZ", "Asia/Shanghai");

var builder = WebApplication.CreateBuilder(args);

// 配置最大请求大小 1GB
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = 1073741824; // 1GB
    options.MemoryBufferThreshold = 10485760; // 10MB in memory before streaming to disk
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1073741824; // 1GB
});

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // 配置JSON序列化将所有时间转换为本地时区（北京时间）输出，方便判断过期
        options.JsonSerializerOptions.Converters.Add(new DateTimeConverter());
    });

// Add PostgreSQL database
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
});

// Add repositories
builder.Services.AddScoped<IFileItemRepository, FileItemRepository>();

// Add permission service
builder.Services.AddScoped<IPermissionService, PermissionService>();

// Add IP whitelist service
builder.Services.AddScoped<IIpWhitelistService, IpWhitelistService>();

// Add background cleanup service
builder.Services.AddHostedService<BackgroundCleanupService>();

// 注册加密相关服务（始终注册，CLI命令和Web服务都需要）
builder.Services.AddSingleton<IKeyProvider, KeyProvider>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var keyFilePath = config.GetValue<string>("Encryption:KeyFilePath")
                      ?? KeyProvider.DefaultKeyFilePath;
    var logger = sp.GetRequiredService<ILogger<KeySlotManager>>();
    return new KeySlotManager(keyFilePath, logger);
});

// 密钥轮换后台服务仅在Web服务模式下运行
var isCliMode = args.Contains("--encrypt-init") || args.Contains("--recover") ||
                args.Contains("--encrypt-add-slot") || args.Contains("--encrypt-remove-slot") ||
                args.Contains("--export-plaintext");
if (!isCliMode)
{
    builder.Services.AddHostedService<KeyRotationService>();
}

// 注册公共路径配置
builder.Services.Configure<PublicPathOptions>(
    builder.Configuration.GetSection("PublicPath"));
builder.Services.AddSingleton<PathMatcher>();
builder.Services.AddSingleton<IPublicFileRateLimiter, PublicFileRateLimiter>();

// 注册 WS 连接管理（单例，整个应用共享连接池）
builder.Services.AddSingleton<WsConnectionManager>();
builder.Services.AddSingleton<ClientRouter>();
builder.Services.AddScoped<WsClientAuthService>();

// 注册消息处理器
builder.Services.AddScoped<IMessageHandler, UploadRequestHandler>();
builder.Services.AddScoped<IMessageHandler, DownloadRequestHandler>();
builder.Services.AddScoped<IMessageHandler, DeleteRequestHandler>();
builder.Services.AddScoped<IMessageHandler, ListRequestHandler>();
builder.Services.AddScoped<IMessageHandler, PingPongHandler>();

// 注册存储策略
builder.Services.AddScoped<IStorageStrategyFactory, StorageStrategyFactory>();
builder.Services.AddScoped<LocalStorageStrategy>();
builder.Services.AddScoped<WsStorageStrategy>();

// 共享下载服务（统一 WS/本地读取 + 透明解密，供网页/API/公共访问共用）
builder.Services.AddScoped<FileDownloadService>();
// 共享删除服务（统一 WS 远程删除 + FileLocation + 本地物理文件清理，供网页/API 共用）
builder.Services.AddScoped<FileDeleteService>();

// 注册限流
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public-file-ip", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });
    options.RejectionStatusCode = 429;
});

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "文件上传下载服务器 API",
        Version = "v1",
        Description = "简单的HTTP文件上传下载服务，支持列表查询、上传、下载、删除"
    });
    
    // 添加API Key支持（query参数）
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "key",
        In = ParameterLocation.Query,
        Description = "输入临时API密钥"
    });
    
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Ensure database is created and tables exist
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<AppDbContext>();
    
    // 使用 Migration 模式，按迁移历史自动应用迁移
    context.Database.Migrate();
}

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "文件上传下载服务器 API v1");
    options.RoutePrefix = "swagger";
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// 限流中间件（尽早应用）
app.UseRateLimiter();

// 公共文件访问中间件（在 API Key 鉴权之前，因为公开访问不需要 key）
// 2026-08-02 曾因"WS 加密文件解密失败 + 中间件直连 WS 节点违背分层架构"而屏蔽；
// 已通过 FileDownloadService 统一"读取 + 解密"逻辑修复，重新启用。
app.UseMiddleware<PublicFileMiddleware>();

// WebSocket 中间件
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseMiddleware<WebSocketHandlerMiddleware>();

app.UseStaticFiles();

// API Key 鉴权中间件 - 在路由之前
app.UseMiddleware<ApiKeyAuthMiddleware>();

app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

// Create uploads directory if it doesn't exist
var uploadsPath = Path.Combine(app.Environment.WebRootPath, "uploads");
if (!Directory.Exists(uploadsPath))
{
    Directory.CreateDirectory(uploadsPath);
}

// 处理加密CLI命令（执行完毕后退出，不启动Web服务）
if (await EncryptionCommands.TryHandleAsync(args, app.Services))
{
    return;
}

app.Run();

// 自定义DateTime转换器，将UTC时间转换为本地时区（北京时间）输出
internal class DateTimeConverter : JsonConverter<DateTime>
{
    private static readonly TimeZoneInfo _localTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // 如果输入是UTC，转换为本地时间再输出
        if (value.Kind == DateTimeKind.Utc)
        {
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(value, _localTimeZone);
            writer.WriteStringValue(localTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"));
        }
        else
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"));
        }
    }
}
