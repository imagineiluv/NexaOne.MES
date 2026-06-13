using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using NexaOne.API.Extensions;
using NexaOne.API.Hubs;
using NexaOne.Application;
using Serilog;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// §17.3 — Serilog 구조화 로그. 싱크/레벨은 appsettings의 Serilog 섹션으로 구성하고,
// 요청 공통 필드(CorrelationId/UserId/PlantId)는 RequestLogContextMiddleware가 주입한다.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Services
// 도메인 enum(상태)을 문자열로 직렬화 — Web DTO(string Status/State)와 계약 일치
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "NexaOne API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// JWT Authentication
var jwtSection = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSection["SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey is required");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSignalR();

// §18.2.3 — Rate Limiting: 기본은 인증 사용자별(없으면 IP별) 100req/min.
// login/forgot-password/register 같은 익명 진입점은 별도 "auth" 정책(IP당 10req/min)으로
// 브루트포스/계정 열거 시도를 차단한다. 거부 응답은 429.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // 설계 표준의 Identity.Name 대신 토큰의 NameIdentifier 클레임 사용 (Name 클레임 부재 적응)
        var userKey = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(userKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// Health checks
builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("NexaOne") ?? string.Empty,
        name: "sqlserver",
        tags: ["db", "sql"]);

// Application services
builder.Services.AddNexaOneServices(builder.Configuration);
builder.Services.AddNexaOneEES(builder.Configuration);
builder.Services.AddScoped<NexaOne.API.Hubs.IEesHubNotifier, NexaOne.API.Hubs.EesHubNotifier>();

// FDC 실시간 수집 호스트 (§10.4.2/10.4.3) — "Fdc:Collector:Enabled"=true 일 때만 실제 기동
// PlantController는 싱글톤으로 공유: HostedService가 설비를 등록·기동하고, FdcController가 수동 제어한다(§10.4.4)
builder.Services.AddSingleton<NexusFramework.PlantController>();
builder.Services.AddHostedService<NexaOne.API.Services.FdcCollectorHostedService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("NexaOne", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors("NexaOne");
app.UseAuthentication();
// §17.3 — 인증 직후 요청 공통 로그 필드 주입(UserId 클레임 판정 가능 시점) + 요청 완료 로그
app.UseMiddleware<NexaOne.API.Middleware.RequestLogContextMiddleware>();
app.UseSerilogRequestLogging();
// §18.2.3 — 사용자/IP 파티션 Rate Limiting (인증 후 → 사용자 클레임 기준 파티션 가능 시점)
app.UseRateLimiter();
// §20.10 — 비밀번호 변경 강제 사용자의 업무 API 차단 (인증 후 → 클레임 판정 가능 시점)
app.UseMiddleware<NexaOne.API.Middleware.PasswordChangeRequiredMiddleware>();
app.UseAuthorization();
app.MapControllers();
// §18.2.3 적응 — SignalR(long-polling 시 분당 요청 수 급증)과 헬스 프로브(공유 모니터링 IP)는
// 전역 한도(100req/min)에 걸려 429가 나면 안 되므로 Rate Limiting에서 제외한다
app.MapHub<NexaOneEESHub>("/hubs/smartees").DisableRateLimiting();
app.MapHealthChecks("/health").DisableRateLimiting();

await app.RunAsync();

public partial class Program { }
