using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Repositories;
using FileUploadServer.Infrastructure.Services;
using FileUploadServer.Web.Middleware;
using FileUploadServer.Web.Services;
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
    
    // Ensure database created and tables created
    context.Database.EnsureCreated();
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
