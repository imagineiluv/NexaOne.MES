using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server.Gateway;
using NexusFramework;
using NexusFramework.Utils;

// 통합 호스트(접근 A, Phase 1) — WebApplication 위에서 Spring.NET 플러그인 컨텍스트 + IHostedService 워커 +
// ASP.NET 파이프라인(HealthChecks·Swagger·JWT)을 한 프로세스로 구동한다. 컨트롤러·UI는 후속 Phase.
var builder = WebApplication.CreateBuilder(args);

var server = ApplicationServer.GetInstance();
var loadedServices = new List<string>();
var workerCount = 0;

// 모듈/플러그인 부트스트랩 — 기본 ON. 웹 셸만 띄우려면(진단/테스트) Server:Modules:Enabled=false 로 끈다.
var modulesEnabled = builder.Configuration.GetValue("Server:Modules:Enabled", true);
if (modulesEnabled)
{
    // SQLite 모드면 컨텍스트 생성 전에 스키마를 부트스트랩한다(빈 DB일 때만, idempotent). server.xml의
    // eesDataSource Provider 타입으로 판별 — XML만 바꾸면 자동 적용(MSSQL이면 아무 일도 안 함).
    EnsureSqliteSchemaIfConfigured("Spring/server.xml");

    var serverCtx = server.CreateServer(new[] { "Spring/server.xml" });
    Console.WriteLine("[NexaOne.Server] Server context initialized.");

    var workers = new List<IHostedService>();
    // 부모(server.xml) 컨텍스트의 IHostedService(예: scheduledOutboxDispatchWorker) 자동발견.
    foreach (IHostedService w in serverCtx.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>())
        workers.Add(w);

    var doc = XDomUtility.Load("Spring/app.xml");
    var root = XDomUtility.GetRoot(doc);
    var services = XDomUtility.GetElement(root, "Services");
    var splitOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
    foreach (var service in XDomUtility.GetElements(services, "Service"))
    {
        var name = service.Attribute("name")?.Value
            ?? throw new InvalidOperationException("Service element missing 'name' attribute.");
        var configFiles = (service.Attribute("configFiles")?.Value
            ?? throw new InvalidOperationException($"Service '{name}' missing 'configFiles' attribute."))
            .Split(';', splitOptions);
        var classPaths = (service.Attribute("classPaths")?.Value
            ?? throw new InvalidOperationException($"Service '{name}' missing 'classPaths' attribute."))
            .Split(';', splitOptions);

        var ctx = server.AddService(name, configFiles, classPaths);
        loadedServices.Add(name);
        Console.WriteLine($"[NexaOne.Server] Service '{name}' registered ({classPaths.Length} module(s)).");

        foreach (IHostedService w in ctx.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>())
            workers.Add(w);
    }

    // 자식 컨텍스트의 GetObjectsOfType은 상속된 부모 빈도 포함 → 인스턴스(참조) 기준 중복 제거.
    var distinctWorkers = workers.Distinct().ToList();
    workerCount = distinctWorkers.Count;
    builder.Services.AddSingleton(server);
    foreach (var w in distinctWorkers)
        builder.Services.AddSingleton<IHostedService>(w);
    Console.WriteLine($"[NexaOne.Server] {distinctWorkers.Count} background worker(s) discovered and registered.");
}
else
{
    Console.WriteLine("[NexaOne.Server] Server:Modules:Enabled=false — 웹 셸만 기동(플러그인/워커 비활성).");
}

// ===== ASP.NET 파이프라인 =====
// 게이트웨이(하이브리드) — 명명 쿼리 데이터 경로(plugin 무관, Default ALC).
builder.Services.AddNexaOneGateway(builder.Configuration);
// 인증(무-브리지, 게이트웨이식) — 토큰 직접 발급(login/refresh). 게이트웨이 DI(IRuleDispatcher) 이후 호출.
builder.Services.AddNexaOneAuth(builder.Configuration);
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// JWT 인증 — API와 동일 규약(강한 비밀키 강제, §18.7). 토큰 발급/컨트롤러는 후속 Phase, 여기선 인증 파이프라인만 활성.
var jwtSection = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSection["SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey is required");
if (secretKey.StartsWith("CHANGE_ME", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(secretKey) < 32)
    throw new InvalidOperationException(
        "Jwt:SecretKey must be a strong secret (>= 32 bytes) supplied via environment variable or user-secrets; "
        + "the committed placeholder is rejected.");
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
    });
builder.Services.AddAuthorization();

// §18.2.3 — 레이트리미터: 익명 인증 진입점(login/refresh)은 "auth"(IP당 10/min), 그 외 전역 100/min.
// RateLimiting:Enabled=false면 미적용(통합테스트가 공유 IP·다수 호출로 비결정 실패하는 것을 피함). 기본 활성.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        });
    });
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
});

var app = builder.Build();

// 개발 SQLite 부트스트랩(게이트웨이 데이터 경로) — Spring 모듈 게이트와 독립. 빈 DB면 db/migrations 스키마 + V001 admin/admin 시드.
if (app.Environment.IsDevelopment()
    && string.Equals(app.Configuration.GetValue<string>("Database:Provider"), "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var gwConn = app.Configuration.GetConnectionString("NexaOne");
    if (!string.IsNullOrWhiteSpace(gwConn))
        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(gwConn);
}

app.UseSwagger();
if (app.Environment.IsDevelopment())
    app.UseSwaggerUI();
app.UseAuthentication();
app.UseMiddleware<NexaOne.Server.Gateway.AuditUserContextMiddleware>();
if (builder.Configuration.GetValue("RateLimiting:Enabled", true))
    app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// /health — 익명(모니터링/k8s liveness). 의존성 체크 없는 기본 생존 체크.
app.MapHealthChecks("/health").AllowAnonymous();

// /diag — 통합 호스트 진단(로드된 Service·워커 수). 인증 필요(인증 파이프라인 활성 입증). 민감정보 없음.
app.MapGet("/diag", () => Results.Ok(new
{
    modulesEnabled,
    services = loadedServices,
    workerCount
})).RequireAuthorization();

// 종료 시 Spring 컨텍스트 정리.
app.Lifetime.ApplicationStopped.Register(() =>
{
    if (modulesEnabled) server.Dispose();
});

Console.WriteLine("[NexaOne.Server] Ready (web host). Press Ctrl+C to stop.");
await app.RunAsync();

// server.xml의 eesDataSource가 SQLite 공급자를 가리키면 해당 ConnectionString에 스키마를 부트스트랩한다.
// MSSQL 모드면 아무 일도 하지 않는다(운영은 마이그레이션 외부 적용). XML 파싱만으로 판별(Spring 컨텍스트와 분리).
static void EnsureSqliteSchemaIfConfigured(string serverXmlPath)
{
    XNamespace ns = "http://www.springframework.net";
    var doc = XDocument.Load(serverXmlPath);
    var objects = doc.Root?.Elements(ns + "object").ToList() ?? new List<XElement>();

    var dataSource = objects.FirstOrDefault(o => (string?)o.Attribute("id") == "eesDataSource");
    if (dataSource is null) return;

    var props = dataSource.Elements(ns + "property").ToList();
    var connStr = props.FirstOrDefault(p => (string?)p.Attribute("name") == "ConnectionString")?.Attribute("value")?.Value;
    var providerRef = props.FirstOrDefault(p => (string?)p.Attribute("name") == "Provider")?.Attribute("ref")?.Value;

    var providerType = objects
        .FirstOrDefault(o => (string?)o.Attribute("id") == providerRef)?
        .Attribute("type")?.Value ?? string.Empty;

    if (!providerType.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;
    if (string.IsNullOrWhiteSpace(connStr))
        throw new InvalidOperationException("SQLite 공급자가 설정됐으나 eesDataSource ConnectionString이 비어 있습니다.");

    Console.WriteLine($"[NexaOne.Server] SQLite mode — ensuring schema ({connStr})...");
    SqliteSchemaInitializer.EnsureSchema(connStr);
    Console.WriteLine("[NexaOne.Server] Schema ready.");
}

// WebApplicationFactory<Program> 진입점 노출(스모크 테스트용).
public partial class Program { }
