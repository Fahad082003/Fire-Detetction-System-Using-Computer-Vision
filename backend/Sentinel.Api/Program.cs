using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.Hubs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;
using Sentinel.Api.Security;
using Sentinel.Api.Services;
using Sentinel.Api.Services.Notifications;

// .env (if present) → environment variables, before configuration is built.
var envFile = DotEnv.Load();

// CLI helper: `dotnet run -- generate-vapid-keys`
if (args.Contains("generate-vapid-keys"))
{
    var keys = WebPush.VapidHelper.GenerateVapidKeys();
    Console.WriteLine($"Notifications__WebPush__PublicKey={keys.PublicKey}");
    Console.WriteLine($"Notifications__WebPush__PrivateKey={keys.PrivateKey}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- options
builder.Services.Configure<AlertOptions>(config.GetSection(AlertOptions.Section));
builder.Services.Configure<DeviceOptions>(config.GetSection(DeviceOptions.Section));
builder.Services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
builder.Services.Configure<NotificationOptions>(config.GetSection(NotificationOptions.Section));
builder.Services.Configure<FloorPlanOptions>(config.GetSection(FloorPlanOptions.Section));
builder.Services.Configure<AiServiceOptions>(config.GetSection(AiServiceOptions.Section));
builder.Services.AddHttpClient<AiInferenceClient>();

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
{
    if (!builder.Environment.IsProduction())
    {
        // Dev convenience: a random key per run (logins expire on restart). Never used in Production.
        jwt.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Console.WriteLine("WARNING: Jwt__SigningKey not set (min 32 chars). Using a temporary random key for this run.");
    }
    else
    {
        throw new InvalidOperationException("Jwt:SigningKey must be configured (at least 32 characters) in Production.");
    }
}
builder.Services.Configure<JwtOptions>(o =>
{
    o.Issuer = jwt.Issuer; o.Audience = jwt.Audience; o.SigningKey = jwt.SigningKey; o.ExpiryMinutes = jwt.ExpiryMinutes;
});

// ---------------------------------------------------------------- data
builder.Services.AddDbContext<SentinelDbContext>(o =>
    o.UseSqlServer(config.GetConnectionString("Sentinel"), sql => sql.EnableRetryOnFailure(3)));

// ---------------------------------------------------------------- application services (DI)
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("camera-stream", c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IncidentLocks>();
builder.Services.AddSingleton<IRealtimeNotifier, RealtimeNotifier>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<INotificationDispatcher>(sp => sp.GetRequiredService<NotificationDispatcher>());
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddSingleton<CameraWatchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraWatchdog>());
builder.Services.AddScoped<INotificationChannel, TwilioSmsChannel>();
builder.Services.AddScoped<INotificationChannel, EmailChannel>();
builder.Services.AddScoped<INotificationChannel, WebPushChannel>();

builder.Services.AddScoped<IAlertEngine, AlertEngine>();
builder.Services.AddScoped<ICameraService, CameraService>();
builder.Services.AddScoped<ISensorService, SensorService>();
builder.Services.AddScoped<ISprinklerService, SprinklerService>();
builder.Services.AddScoped<IIncidentService, IncidentService>();
builder.Services.AddScoped<IDetectionService, DetectionService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPushSubscriptionService, PushSubscriptionService>();
builder.Services.AddScoped<IFloorPlanService, FloorPlanService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<DbSeeder>();

// ---------------------------------------------------------------- web: JSON, SignalR, CORS, rate limit
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 512 * 1024)
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy("Frontend", p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// ---------------------------------------------------------------- authentication / authorization
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = ClaimTypes.Name, RoleClaimType = ClaimTypes.Role,
        };
        // Browsers cannot set headers on WebSockets or <img>, so allow ?access_token= for the hub and MJPEG stream.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(token) && (path.StartsWithSegments("/hubs") || path.Value?.EndsWith("/stream") == true))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(AuthPolicies.DeviceScheme, _ => { });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(AuthPolicies.Operator, p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireRole(nameof(UserRole.Admin), nameof(UserRole.Operator)));
    o.AddPolicy(AuthPolicies.Admin, p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireRole(nameof(UserRole.Admin)));
    o.AddPolicy(AuthPolicies.Device, p => p
        .AddAuthenticationSchemes(AuthPolicies.DeviceScheme)
        .RequireRole(AuthPolicies.DeviceRole));
    o.AddPolicy(AuthPolicies.OperatorOrDevice, p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthPolicies.DeviceScheme)
        .RequireRole(nameof(UserRole.Admin), nameof(UserRole.Operator), AuthPolicies.DeviceRole));
});

// ---------------------------------------------------------------- Swagger (Development)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "FireVision AI Emergency Monitoring API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Paste the token from POST /api/auth/login",
    });
    o.AddSecurityDefinition("DeviceKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = AuthPolicies.DeviceHeader,
        Description = "Device key for AI pipelines / IoT gateways (Device__ApiKey)",
    });
    OpenApiSecurityScheme Ref(string id) => new() { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = id } };
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [Ref("Bearer")] = [], [Ref("DeviceKey")] = [] });
});

var app = builder.Build();

// ---------------------------------------------------------------- HTTP pipeline (order matters)
app.UseMiddleware<ExceptionHandlingMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "FireVision AI API");
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<EmergencyMonitoringHub>("/hubs/monitoring");
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow })).AllowAnonymous();

if (envFile is not null) app.Logger.LogInformation("Loaded environment from {File}", envFile);

// First-run data (admin account + demo devices). Skipped in the "Testing" environment.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DbSeeder>().RunAsync();
}

app.Run();

/// <summary>Exposed for WebApplicationFactory integration tests.</summary>
public partial class Program;
