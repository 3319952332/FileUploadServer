using Microsoft.OpenApi.Models;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.Infrastructure.Data;
using FileUploadServer.Infrastructure.Repositories;
using FileUploadServer.Web.Middleware;
using Microsoft.EntityFrameworkCore;

// 设置时区为北京时间
TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
Environment.SetEnvironmentVariable("TZ", "Asia/Shanghai");

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// Add PostgreSQL database
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
});

// Add repositories
builder.Services.AddScoped<IFileItemRepository, FileItemRepository>();

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
