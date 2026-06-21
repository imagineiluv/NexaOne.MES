using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Components.Authorization;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server.Components;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Rms;
using NexaOne.ServiceContracts.Shp;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;
using NexaOne.Web.Services.Meta;
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
    // Spring 부모 컨텍스트 설정 경로 — 기본은 운영 server.xml(MSSQL). 테스트/로컬은 Server:SpringConfig로
    // SQLite 변형(Spring/server.sqlite.xml)을 가리켜 외부 DB 없이 modules-ON 부팅을 검증한다(데이터소스만 다른 동일 빈 집합).
    var springConfig = builder.Configuration.GetValue("Server:SpringConfig", "Spring/server.xml")!;

    // SQLite 모드면 컨텍스트 생성 전에 스키마를 부트스트랩한다(빈 DB일 때만, idempotent). server.xml의
    // eesDataSource Provider 타입으로 판별 — XML만 바꾸면 자동 적용(MSSQL이면 아무 일도 안 함).
    EnsureSqliteSchemaIfConfigured(springConfig);

    var serverCtx = server.CreateServer(new[] { springConfig });
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

    // 복잡 서비스 얇은 브리지(ADR-008) — EST 설비상태 빈을 공유 계약 인터페이스로 캐스트해 DI 등록.
    // 캐스트 실패 = 계약 어셈블리 ALC 동일성 위반(deps-제외 누락 등) → 기동 시 즉시 폭발(무음 런타임 실패 방지).
    var equipmentStateBridge = server.GetBean("Est", "equipmentStateBridge") as IEquipmentStateBridge
        ?? throw new InvalidOperationException(
            "equipmentStateBridge 빈을 IEquipmentStateBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts가 plugin ALC로 복제 로드되지 않았는지(ADR-008/모듈 게시 deps-제외) 확인하세요.");
    builder.Services.AddSingleton(equipmentStateBridge);

    // ADR-008 얇은 브리지 — EST 설비알람. 상태 브리지와 동일 메커니즘(GetBean→캐스트→fail-fast 등록).
    var equipmentAlarmBridge = server.GetBean("Est", "equipmentAlarmBridge") as IEquipmentAlarmBridge
        ?? throw new InvalidOperationException(
            "equipmentAlarmBridge 빈을 IEquipmentAlarmBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts가 plugin ALC로 복제 로드되지 않았는지(ADR-008/모듈 게시 deps-제외) 확인하세요.");
    builder.Services.AddSingleton(equipmentAlarmBridge);

    // ADR-008 얇은 브리지 — RMS 레시피 승인. EST와 동일 메커니즘(GetBean→캐스트→fail-fast 등록).
    var rmsRecipeBridge = server.GetBean("Rms", "rmsRecipeBridge") as IRecipeApprovalBridge
        ?? throw new InvalidOperationException(
            "rmsRecipeBridge 빈을 IRecipeApprovalBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(rmsRecipeBridge);

    // ADR-008 얇은 브리지 — SHP 출하주문 생명주기. EST/RMS와 동일 메커니즘(GetBean→캐스트→fail-fast 등록).
    var shipmentBridge = server.GetBean("Shp", "shipmentBridge") as IShipmentBridge
        ?? throw new InvalidOperationException(
            "shipmentBridge 빈을 IShipmentBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(shipmentBridge);

    // ADR-008 얇은 브리지 — QMS 부적합 확정·SPC 관리한계 갱신. EST/RMS/SHP와 동일 메커니즘(GetBean→캐스트→fail-fast 등록).
    var qmsBridge = server.GetBean("Qms", "qmsBridge") as IQmsBridge
        ?? throw new InvalidOperationException(
            "qmsBridge 빈을 IQmsBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(qmsBridge);

    // ADR-008 얇은 브리지 — MDM 설비 생성/비활성/갱신(불변식). 첫 미노출-모듈 브리지 부팅 —
    // 캐스트 성공 = MDM plugin ALC가 공유 ServiceContracts 계약을 Default ALC와 동일 타입으로 보는지 입증.
    var mdmEquipmentBridge = server.GetBean("Mdm", "mdmEquipmentBridge") as IMdmEquipmentBridge
        ?? throw new InvalidOperationException(
            "mdmEquipmentBridge 빈을 IMdmEquipmentBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(mdmEquipmentBridge);

    // ADR-008 얇은 브리지 — MDM 마스터(Plant/Area/Product/CodeClass/Code) 생성. EST/RMS/SHP/QMS와 동일 메커니즘.
    var mdmMasterBridge = server.GetBean("Mdm", "mdmMasterBridge") as IMdmMasterBridge
        ?? throw new InvalidOperationException(
            "mdmMasterBridge 빈을 IMdmMasterBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(mdmMasterBridge);
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

// ===== Blazor 슬라이스(Phase 4 Task 3) — RCL(NexaOne.Web.Components)의 /meta + 호스트 로컬 로그인 =====
// 단일 JwtBearer 유지(설계 §4): 화면 [Authorize]는 클라이언트측 JwtAuthStateProvider(세션 토큰)가 평가하며
// 위 서버 JwtBearer 스킴과 독립이다. 쿠키 스킴·DevAutoAuthHandler는 호스트에 등록하지 않는다(prerender:false라 불필요).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());
builder.Services.AddScoped<AuthTokenService>();
builder.Services.AddScoped<AuthContextService>();
builder.Services.AddScoped<IAuthContext>(sp => sp.GetRequiredService<AuthContextService>());
// API 실패(403/5xx)를 토스트로 노출하는 통지 채널 — ApiClient가 발신(슬라이스 최소 폐포; ApiToastHost UI는 미흡수).
builder.Services.AddScoped<ApiNotificationService>();
// Phase 5a — DB-backed 화면정의 제공자(게이트웨이 SYS.GetScreenDefinition, InMemory 시드 폴백).
builder.Services.AddSingleton<IScreenDefinitionProvider>(sp => new GatewayScreenDefinitionProvider(
    sp.GetRequiredService<IRuleDispatcher>(), sp.GetRequiredService<IQueryRegistry>()));
// ApiClient BaseAddress = 호스트 자기 origin(설계 §4) — /meta가 쓰는 query/command/auth가 모두 이 호스트에 존재.
// ApiBaseUrl 미설정 시 Server:Port(기본 8080)로 자기 origin을 구성한다(NexaOne.Web과 달리 예외 없이 기본값).
var hostApiBase = builder.Configuration["ApiBaseUrl"]
    ?? $"http://localhost:{builder.Configuration.GetValue("Server:Port", 8080)}/";
builder.Services.AddTransient<DefaultRequestTimeoutHandler>();
builder.Services.AddHttpClient<IApiClient, ApiClient>(c =>
{
    c.BaseAddress = new Uri(hostApiBase);
    // §20.11: 전역 Timeout은 대용량 업로드 상한 — 일반 요청은 DefaultRequestTimeoutHandler가 기본 100초로 제한.
    c.Timeout = TimeSpan.FromMinutes(10);
}).AddHttpMessageHandler<DefaultRequestTimeoutHandler>();

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
// React SPA 정적 서빙(Phase 4) — wwwroot/spa의 빌드 산출물을 공개 자산으로 인증 전에 서빙한다.
// (정적 SPA 자산은 공개; 일반적 ASP.NET 순서에 맞춰 인증 미들웨어보다 앞에 둔다.)
app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<NexaOne.Server.Gateway.AuditUserContextMiddleware>();
if (builder.Configuration.GetValue("RateLimiting:Enabled", true))
    app.UseRateLimiter();
app.UseAuthorization();
// 비밀번호 강제 변경(pwdChange) 사용자의 업무 데이터 호출 차단 — 인증 이후, 엔드포인트 실행 이전.
app.UseMiddleware<NexaOne.Server.Gateway.PasswordChangeRequiredMiddleware>();
// Blazor 폼/컴포넌트 보호용 안티포저리(설계 §5). [ApiController] JSON 엔드포인트(api/v1/*)는 폼 콘텐츠가 아니라
// 영향받지 않는다 — 안티포저리는 form-data/urlencoded 또는 명시 [RequireAntiforgeryToken]에만 적용된다.
app.UseAntiforgery();

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

// Blazor 슬라이스(Phase 4 Task 3) — /meta/{uiId}(RCL) + 호스트 로컬 /login + /_blazor circuit.
// prerender:false(설계 §4) — HostApp의 InteractiveServerRenderMode가 명시한다. MapControllers·/health·/diag
// 뒤, MapFallbackToFile 앞에 둔다(명시 라우트 우선, SPA nonfile 폴백은 최후순).
// AddAdditionalAssemblies(RCL): 라우트 가능한 컴포넌트 엔드포인트 탐색은 루트(HostApp) 어셈블리만 스캔하므로,
// RCL(NexaOne.Web.Components)의 /meta/{uiId}는 명시적으로 추가해야 서버측 엔드포인트로 등록된다. HostRoutes의
// <Router AdditionalAssemblies>는 서킷(클라이언트) 라우터용이라 서버 엔드포인트 매핑에는 영향을 주지 않는다.
app.MapRazorComponents<HostApp>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(NexaOne.Web.Pages.Meta.MetaScreen).Assembly);

// SPA 폴백(Phase 4) — 명시적 api/v1·Blazor 라우트가 우선한다. 파일이 아닌 /spa/* 경로만 React BrowserRouter
// 셸(index.html)로 폴백한다(엔드포인트 매핑 최후순으로 두어 다른 라우트가 가려지지 않게 한다).
app.MapFallbackToFile("/spa/{*path:nonfile}", "/spa/index.html");

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
