using FileUploadServer.Auth.Data;
using FileUploadServer.Auth.Dtos;
using FileUploadServer.Auth.Entities;
using FileUploadServer.Auth.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 数据库（与文件服同一 PostgreSQL 实例，独立迁移历史表避免冲突）
builder.Services.AddDbContext<AuthDbContext>(options =>
{
    var conn = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(conn, b => b.MigrationsHistoryTable("__EFMigrationsHistory_Auth"));
});

// 文件服本地 admin 通道
builder.Services.AddHttpClient<FileServerAdminClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddScoped<AccountKeyService>();
builder.Services.AddHostedService<AuthKeyRenewalService>();

// 限流（防爆破/防注册滥用）
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth-login", o =>
    {
        o.PermitLimit = 30;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 5;
    });
    options.AddFixedWindowLimiter("auth-register", o =>
    {
        o.PermitLimit = 10;
        o.Window = TimeSpan.FromMinutes(10);
        o.QueueLimit = 0;
    });
    options.RejectionStatusCode = 429;
});

var app = builder.Build();

// 启动时迁移建表 + 种子管理员
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    db.Database.Migrate();
    await SeedAdminAsync(db, app.Configuration);
}

var authSection = app.Configuration.GetSection("Auth");
var sessionTtl = TimeSpan.FromDays(authSection.GetValue("SessionTtlDays", 30));
var registrationEnabled = authSection.GetValue("RegistrationEnabled", true);

app.UseRateLimiter();

// ========== 辅助函数 ==========

static string Normalize(string username) => username.Trim().ToLowerInvariant();

static bool IsLocalRequest(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress != null && System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);

static string? GetBearerToken(HttpContext ctx)
{
    if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
        return null;
    var value = header.ToString();
    return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? value["Bearer ".Length..].Trim()
        : null;
}

/// <summary>按 Bearer 令牌解析当前 Active 用户（无/失效返回 null）</summary>
static async Task<UserDto?> GetUserFromRequestAsync(HttpContext ctx, AuthDbContext db)
{
    var token = GetBearerToken(ctx);
    if (token == null)
        return null;
    var hash = SessionTokenService.HashToken(token);
    var session = await db.Sessions.FirstOrDefaultAsync(s =>
        s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow);
    if (session == null)
        return null;
    var user = await db.Users.FirstOrDefaultAsync(u =>
        u.Id == session.UserId && u.DeletedAt == null && u.Status == "Active");
    if (user == null)
        return null;
    return new UserDto(user.Id, user.Username, user.IsAdmin, user.Status, user.ApprovedAt);
}

/// <summary>管理员校验：仅本机请求 + 管理员会话（审核注册等管理操作）</summary>
static async Task<AuthUser?> GetAdminUserAsync(HttpContext ctx, AuthDbContext db)
{
    if (!IsLocalRequest(ctx))
        return null;
    var dto = await GetUserFromRequestAsync(ctx, db);
    if (dto == null || !dto.IsAdmin)
        return null;
    return await db.Users.FirstAsync(u => u.Id == dto.Id);
}

static async Task SeedAdminAsync(AuthDbContext db, IConfiguration config)
{
    // 种子管理员只从环境变量读取（不在配置文件/appsettings.json 存明文初始密码）
    var adminName = Environment.GetEnvironmentVariable("AUTH_SEED_ADMIN_USERNAME");
    var adminPwd = Environment.GetEnvironmentVariable("AUTH_SEED_ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(adminName) || string.IsNullOrWhiteSpace(adminPwd))
        return;
    adminName = Normalize(adminName);
    if (await db.Users.AnyAsync(u => u.Username == adminName))
        return;
    db.Users.Add(new AuthUser
    {
        Username = adminName,
        PasswordHash = PasswordHasher.Hash(adminPwd),
        Status = "Active",
        IsAdmin = true,
        CreatedAt = DateTime.UtcNow,
        ApprovedAt = DateTime.UtcNow,
    });
    await db.SaveChangesAsync();
    Console.WriteLine($"[Auth] 已创建种子管理员账号: {adminName}（初始密码来自环境变量，请尽快登录修改）");
}

// ========== 认证 API ==========

// 注册（申请制：创建待审核账号，管理员同意后才生效）
app.MapPost("/api/auth/register", async (RegisterRequest req, AuthDbContext db) =>
{
    if (!registrationEnabled)
        return Results.BadRequest(new ApiMessage("注册已关闭"));
    var username = Normalize(req.Username);
    if (username.Length < 3 || username.Length > 64)
        return Results.BadRequest(new ApiMessage("用户名长度需为 3-64"));
    if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 6 || req.Password.Length > 128)
        return Results.BadRequest(new ApiMessage("密码长度需为 6-128"));
    if (await db.Users.AnyAsync(u => u.Username == username))
        return Results.Conflict(new ApiMessage("用户名已存在"));

    db.Users.Add(new AuthUser
    {
        Username = username,
        PasswordHash = PasswordHasher.Hash(req.Password),
        Status = "Pending",
        CreatedAt = DateTime.UtcNow,
    });
    await db.SaveChangesAsync();
    return Results.Created($"/api/auth/admin/users", new ApiMessage("注册成功，等待管理员审核"));
}).RequireRateLimiting("auth-register");

// 登录：校验账密 → 激活/续期账号密钥 → 签发会话令牌 → 下发 token + fileKey
app.MapPost("/api/auth/login", async (LoginRequest req, AuthDbContext db, AccountKeyService keyService) =>
{
    var username = Normalize(req.Username);
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username && u.DeletedAt == null);
    if (user == null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
        return Results.Unauthorized();
    if (user.Status == "Pending")
        return Results.Json(new ApiMessage("账号待管理员审核"), statusCode: StatusCodes.Status403Forbidden);
    if (user.Status == "Disabled")
        return Results.Json(new ApiMessage("账号已被禁用"), statusCode: StatusCodes.Status403Forbidden);

    // 向文件服确保/续期账号密钥（管理员 → Admin 型密钥，可见全部文件）
    var (fileKey, fileKeyExpiresAt) = await keyService.EnsureValidAsync(user.Id, user.Username, user.IsAdmin);

    // 签发会话
    var token = SessionTokenService.GenerateToken();
    db.Sessions.Add(new AuthSession
    {
        UserId = user.Id,
        TokenHash = SessionTokenService.HashToken(token),
        DeviceName = req.DeviceName ?? string.Empty,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.Add(sessionTtl),
    });
    await db.SaveChangesAsync();

    var dto = new UserDto(user.Id, user.Username, user.IsAdmin, user.Status, user.ApprovedAt);
    return Results.Ok(new LoginResponse(token, DateTime.UtcNow.Add(sessionTtl), dto, fileKey, fileKeyExpiresAt));
}).RequireRateLimiting("auth-login");

// 登出：吊销当前会话
app.MapPost("/api/auth/logout", async (HttpContext ctx, AuthDbContext db) =>
{
    var token = GetBearerToken(ctx);
    if (token != null)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == SessionTokenService.HashToken(token));
        if (session != null)
        {
            session.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
    return Results.Ok(new ApiMessage("已登出"));
});

// 修改密码：验证原密码 → 设置新密码 → 吊销除当前会话外的所有会话（防旧令牌继续有效）
app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, HttpContext ctx, AuthDbContext db) =>
{
    var dto = await GetUserFromRequestAsync(ctx, db);
    if (dto == null)
        return Results.Unauthorized();

    var user = await db.Users.FirstAsync(u => u.Id == dto.Id);
    if (!PasswordHasher.Verify(req.OldPassword, user.PasswordHash))
        return Results.Json(new ApiMessage("原密码不正确"), statusCode: StatusCodes.Status400BadRequest);
    if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 6 || req.NewPassword.Length > 128)
        return Results.BadRequest(new ApiMessage("新密码长度需为 6-128"));

    user.PasswordHash = PasswordHasher.Hash(req.NewPassword);

    // 吊销该用户其他会话（保留当前会话）
    var currentHash = SessionTokenService.HashToken(GetBearerToken(ctx) ?? "");
    var others = await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null && s.TokenHash != currentHash)
        .ToListAsync();
    foreach (var s in others)
        s.RevokedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new ApiMessage("密码已修改，其他设备已下线"));
});

// me：当前用户信息（自动登录校验用）
app.MapGet("/api/auth/me", async (HttpContext ctx, AuthDbContext db) =>
{
    var user = await GetUserFromRequestAsync(ctx, db);
    return user == null ? Results.Unauthorized() : Results.Ok(user);
});

// refresh：续签会话有效期，返回新过期时间
app.MapPost("/api/auth/refresh", async (HttpContext ctx, AuthDbContext db) =>
{
    var token = GetBearerToken(ctx);
    if (token == null)
        return Results.Unauthorized();
    var hash = SessionTokenService.HashToken(token);
    var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null);
    if (session == null || session.ExpiresAt < DateTime.UtcNow)
        return Results.Unauthorized();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId && u.DeletedAt == null && u.Status == "Active");
    if (user == null)
        return Results.Unauthorized();

    session.ExpiresAt = DateTime.UtcNow.Add(sessionTtl);
    await db.SaveChangesAsync();
    var dto = new UserDto(user.Id, user.Username, user.IsAdmin, user.Status, user.ApprovedAt);
    return Results.Ok(new LoginResponse(token, session.ExpiresAt, dto, string.Empty, DateTime.UtcNow));
});

// session/verify：局域网接收器校验对端令牌（PC/手机直传前调用）
app.MapPost("/api/auth/session/verify", async (HttpContext ctx, AuthDbContext db) =>
{
    var user = await GetUserFromRequestAsync(ctx, db);
    return user == null
        ? Results.Ok(new VerifyResponse(false, null, null))
        : Results.Ok(new VerifyResponse(true, user.Id, user.Username));
});

// ========== 管理 API（仅本机 + 管理员会话） ==========

app.MapGet("/api/auth/admin/users", async (HttpContext ctx, AuthDbContext db) =>
{
    if (!IsLocalRequest(ctx))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var users = await db.Users.Where(u => u.DeletedAt == null)
        .OrderByDescending(u => u.CreatedAt)
        .Select(u => new UserDto(u.Id, u.Username, u.IsAdmin, u.Status, u.ApprovedAt))
        .ToListAsync();
    return Results.Ok(users);
});

app.MapPut("/api/auth/admin/users/{id:int}/approve", async (int id, HttpContext ctx, AuthDbContext db) =>
{
    var admin = await GetAdminUserAsync(ctx, db);
    if (admin == null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var user = await db.Users.FindAsync(id);
    if (user == null || user.DeletedAt != null)
        return Results.NotFound();
    user.Status = "Active";
    user.ApprovedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new UserDto(user.Id, user.Username, user.IsAdmin, user.Status, user.ApprovedAt));
});

app.MapPut("/api/auth/admin/users/{id:int}/disable", async (int id, HttpContext ctx, AuthDbContext db) =>
{
    var admin = await GetAdminUserAsync(ctx, db);
    if (admin == null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (id == admin.Id)
        return Results.BadRequest(new ApiMessage("不能禁用自己"));
    var user = await db.Users.FindAsync(id);
    if (user == null || user.DeletedAt != null)
        return Results.NotFound();
    user.Status = "Disabled";
    await db.SaveChangesAsync();
    return Results.Ok(new UserDto(user.Id, user.Username, user.IsAdmin, user.Status, user.ApprovedAt));
});

app.MapPut("/api/auth/admin/users/{id:int}/reset-password", async (int id, ResetPasswordRequest req, HttpContext ctx, AuthDbContext db) =>
{
    var admin = await GetAdminUserAsync(ctx, db);
    if (admin == null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 6 || req.Password.Length > 128)
        return Results.BadRequest(new ApiMessage("密码长度需为 6-128"));
    var user = await db.Users.FindAsync(id);
    if (user == null || user.DeletedAt != null)
        return Results.NotFound();
    user.PasswordHash = PasswordHasher.Hash(req.Password);
    await db.SaveChangesAsync();
    return Results.Ok(new ApiMessage("密码已重置"));
});

// 删除账号：软删 + 吊销会话 + 回收文件服账号密钥
app.MapDelete("/api/auth/admin/users/{id:int}", async (int id, HttpContext ctx, AuthDbContext db, FileServerAdminClient fileServer) =>
{
    var admin = await GetAdminUserAsync(ctx, db);
    if (admin == null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (id == admin.Id)
        return Results.BadRequest(new ApiMessage("不能删除自己"));
    var user = await db.Users.FindAsync(id);
    if (user == null || user.DeletedAt != null)
        return Results.NotFound();

    user.DeletedAt = DateTime.UtcNow;
    foreach (var s in await db.Sessions.Where(s => s.UserId == id).ToListAsync())
        s.RevokedAt = DateTime.UtcNow;

    var ak = await db.AccountKeys.FirstOrDefaultAsync(x => x.UserId == id);
    if (ak != null)
    {
        try
        {
            await fileServer.DeleteKeyAsync(ak.FileKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] 删除文件服密钥失败 fileKeyId={ak.FileKeyId}: {ex.Message}");
        }
        db.AccountKeys.Remove(ak);
    }
    await db.SaveChangesAsync();
    return Results.Ok(new ApiMessage("账号已删除"));
});

app.Run();
