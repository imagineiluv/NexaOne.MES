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
using NexaOne.ServiceContracts.Cmms;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Rms;
using NexaOne.ServiceContracts.Shp;
using NexaOne.ServiceContracts.Sys;
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
    // Spring 부모 컨텍스트 설정 경로 — 기본은 운영 config/host/server.xml(MSSQL). 테스트/로컬은 Server:SpringConfig로
    // SQLite 변형(config/host/server.sqlite.xml)을 가리켜 외부 DB 없이 modules-ON 부팅을 검증한다(데이터소스만 다른 동일 빈 집합).
    var springConfig = builder.Configuration.GetValue("Server:SpringConfig", "config/host/server.xml")!;

    // SQLite 모드면 컨텍스트 생성 전에 스키마를 부트스트랩한다(빈 DB일 때만, idempotent). server.xml의
    // eesDataSource Provider 타입으로 판별 — XML만 바꾸면 자동 적용(MSSQL이면 아무 일도 안 함).
    EnsureSqliteSchemaIfConfigured(springConfig);

    var serverCtx = server.CreateServer(new[] { springConfig });
    Console.WriteLine("[NexaOne.Server] Server context initialized.");

    var workers = new List<IHostedService>();
    // 부모(server.xml) 컨텍스트의 IHostedService(예: scheduledOutboxDispatchWorker) 자동발견.
    foreach (IHostedService w in serverCtx.GetObjectsOfType(typeof(IHostedService)).Values.Cast<IHostedService>())
        workers.Add(w);

    var doc = XDomUtility.Load("config/app.xml");
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

    // ADR-008 얇은 브리지 — CMMS 보전(작업지시/보전계획/예비품) 단일 애그리거트 쓰기. EST/RMS/SHP/QMS/MDM과 동일 메커니즘.
    var cmmsBridge = server.GetBean("Cmms", "cmmsBridge") as ICmmsBridge
        ?? throw new InvalidOperationException(
            "cmmsBridge 빈을 ICmmsBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(cmmsBridge);

    // ADR-008 얇은 브리지 — POM 생산(계획/오더/Lot 추적) 단일 애그리거트 쓰기. EST/RMS/SHP/QMS/MDM/CMMS와 동일 메커니즘.
    // Lot Mixing(다중 애그리거트)은 브리지에서 제외(UnitOfWork 선결).
    var pomBridge = server.GetBean("Pom", "pomBridge") as IPomBridge
        ?? throw new InvalidOperationException(
            "pomBridge 빈을 IPomBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(pomBridge);

    // ADR-008 얇은 브리지 — SYS 비-자격증명 단일 애그리거트 쓰기(역할 관리·신청 반려·사용자 비활성). EST/RMS/SHP/QMS/MDM/CMMS/POM과 동일 메커니즘.
    // 보안 가드(S7): 자격증명/비밀번호/로그인·승인(다중 애그리거트)·잠금 해제는 본 브리지에서 제외(인증 경로·UnitOfWork 선결 소유).
    var sysBridge = server.GetBean("Sys", "sysBridge") as ISysBridge
        ?? throw new InvalidOperationException(
            "sysBridge 빈을 ISysBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(sysBridge);

    // ADR-008 얇은 브리지 — FDC 비-실시간 설정 관리(파라미터그룹/알람설정/인터락규칙 생성) 단일 애그리거트 쓰기. EST/RMS/SHP/QMS/MDM/CMMS/POM/SYS와 동일 메커니즘.
    // 워커 가드(S8): 실시간 수집/평가·OPC-UA·발생/해제 이력·수집데이터 기록은 워커 소유라 본 브리지에서 제외(ADR-006, REST 비노출).
    var fdcBridge = server.GetBean("Fdc", "fdcBridge") as IFdcBridge
        ?? throw new InvalidOperationException(
            "fdcBridge 빈을 IFdcBridge로 캐스트하지 못했습니다 — "
            + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
    builder.Services.AddSingleton(fdcBridge);
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
// MDI 탭 상태(서킷당 1개) — MesShellLayout이 /meta 내비게이션으로 열린 화면 탭을 추적/렌더한다.
builder.Services.AddScoped<NexaOne.Server.Services.OpenedScreensState>();
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
    {
        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(gwConn);
        // 스키마 보장 후 SmartUX 셸 사이드바를 채울 계층 메뉴를 시드한다(SYS_MENU 비었을 때만, idempotent, Dev 전용).
        SeedDevMenuIfEmpty(gwConn);
    }
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
// AllowAnonymous(설계 §4 정합): Blazor 엔드포인트는 익명으로 셸을 서빙하고, 화면 인가는 클라이언트측
// AuthorizeRouteView(JwtAuthStateProvider, 세션 토큰)가 담당한다. MetaScreen의 @attribute [Authorize]가
// 엔드포인트 인가로 승격되면(.NET 8 기본) 브라우저 내비게이션(Authorization 헤더 없음, 토큰은 세션스토리지)이
// 401로 막혀 로그인 후 화면이 안 뜬다 — prerender:false라 GET은 컴포넌트 본문을 서버렌더하지 않아 셸엔 보호
// 데이터가 없고, 실제 데이터는 Bearer 보호 API(/api/v1/*)가 게이트하므로 셸 익명 서빙은 안전하다. 미인증
// 직접 진입은 셸 로드→서킷→AuthorizeRouteView가 /login으로 리다이렉트(401 에러 아님).
app.MapRazorComponents<HostApp>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(NexaOne.Web.Pages.Meta.MetaScreen).Assembly)
    .AllowAnonymous();

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

// 개발 SQLite 전용 — SYS_MENU가 비어 있을 때만 SmartUX(:9020) 실제 데스크톱 메뉴 트리를 시드한다(idempotent).
// 임베드된 smartux-menu.json(SUX 카테고리 331행, 4단계 계층) + 동작하는 데모/관리 화면 폴더를 덧붙인다. 운영(MSSQL)은
// 본 경로를 타지 않는다(상위 if가 Database:Provider==Sqlite && Development일 때만 호출). 직접 Dapper-free
// Microsoft.Data.Sqlite 인서트 — 게이트웨이 DI/감사 컨텍스트 없이 부트스트랩 시점에 안전하게 채운다.
static void SeedDevMenuIfEmpty(string connectionString)
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
    conn.Open();

    using (var count = conn.CreateCommand())
    {
        count.CommandText = "SELECT COUNT(*) FROM SYS_MENU";
        if (Convert.ToInt64(count.ExecuteScalar() ?? 0L) > 0) return; // 이미 시드됨/사용자 데이터 존재 → 건너뜀
    }

    // 임베드된 SmartUX 메뉴 트리 우선. 리소스 부재 시 최소 폴백(셸이 빈 사이드바가 되지 않게).
    var rows = LoadSmartUxMenuSeed() ?? MinimalFallbackMenu();

    using var tx = conn.BeginTransaction();
    foreach (var r in rows)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO SYS_MENU (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, UI_ID, VALID_STATE) " +
            "VALUES (@id, @name, @parent, @seq, @type, @uiId, 'Valid')";
        cmd.Parameters.AddWithValue("@id", r.MenuId);
        cmd.Parameters.AddWithValue("@name", r.MenuName);
        cmd.Parameters.AddWithValue("@parent", (object?)r.ParentMenuId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@seq", r.DisplaySequence);
        cmd.Parameters.AddWithValue("@type", r.MenuType);
        cmd.Parameters.AddWithValue("@uiId", (object?)r.UiId ?? "");
        cmd.ExecuteNonQuery();
    }
    tx.Commit();
    Console.WriteLine($"[NexaOne.Server] SYS_MENU seeded ({rows.Count} rows: SmartUX tree + dev-demo).");
}

// 임베드된 smartux-menu.json(SUX 데스크톱 트리)를 로드하고, 실제 동작하는 데모/관리 화면을 별도 폴더로 덧붙여 반환한다.
// 리소스가 없거나 비면 null을 반환(호출부가 최소 폴백 사용).
static List<MenuSeedRow>? LoadSmartUxMenuSeed()
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var name = asm.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith("smartux-menu.json", StringComparison.OrdinalIgnoreCase));
    if (name is null) return null;
    using var stream = asm.GetManifestResourceStream(name);
    if (stream is null) return null;
    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
    var rows = System.Text.Json.JsonSerializer.Deserialize<List<MenuSeedRow>>(
        reader.ReadToEnd(),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (rows is null || rows.Count == 0) return null;
    rows.AddRange(DevDemoMenu());
    return rows;
}

// 실제 백엔드가 있어 '동작하는' 데모/관리 화면 — SmartUX 트리와 명확히 구분된 별도 폴더(맨 끝 정렬)로 노출해
// 메뉴 관리 등 핵심 화면이 사이드바에서 항상 접근 가능하게 한다. SmartUX 화면이 마이그될수록 본 폴더 의존은 줄어든다.
static IEnumerable<MenuSeedRow> DevDemoMenu() => new[]
{
    new MenuSeedRow("NX_DEV",        "● NexaOne 데모/관리",   null,     9000, "Folder", ""),
    new MenuSeedRow("NX_DEV_MENU",   "메뉴 관리",             "NX_DEV", 10,   "Screen", "SYS_MENU_MGMT"),
    new MenuSeedRow("NX_DEV_GRID",   "공장 관리(데모)",        "NX_DEV", 20,   "Screen", "DEMO_GRID"),
    new MenuSeedRow("NX_DEV_LAYOUT", "생산 현황(데모)",        "NX_DEV", 30,   "Screen", "DEMO_LAYOUT"),
    new MenuSeedRow("NX_DEV_PARAM",  "파라미터 입력(데모)",     "NX_DEV", 40,   "Screen", "DEMO_PARAM"),
    new MenuSeedRow("NX_DEV_DEFECT", "결함 분류(데모)",        "NX_DEV", 50,   "Screen", "DEMO_QMS_DEFECT_CLASS"),
    new MenuSeedRow("NX_DEV_PLANT",  "공장 폼(데모)",          "NX_DEV", 60,   "Screen", "DEMO_PLANT_FORM"),
};

// 임베드 리소스가 없을 때의 최소 폴백 트리(셸이 절대 빈 사이드바가 되지 않게).
static List<MenuSeedRow> MinimalFallbackMenu() => new()
{
    new("M_STD", "기준정보", null, 10, "Folder", ""),
    new("M_STD_PLANT", "공장 관리", "M_STD", 10, "Screen", "DEMO_GRID"),
    new("M_PRD", "생산관리", null, 20, "Folder", ""),
    new("M_PRD_STATUS", "생산 현황", "M_PRD", 10, "Screen", "DEMO_LAYOUT"),
    new("M_SYS", "시스템관리", null, 90, "Folder", ""),
    new("M_SYS_MENU", "메뉴 관리", "M_SYS", 10, "Screen", "SYS_MENU_MGMT"),
};

// smartux-menu.json 한 행(camelCase JSON ↔ PascalCase 매핑은 PropertyNameCaseInsensitive로 처리).
// UiId는 Screen에만 채워진다(Folder는 공백 → 클릭=토글). ParentMenuId는 최상위에서 null.
internal sealed record MenuSeedRow(
    string MenuId, string MenuName, string? ParentMenuId, int DisplaySequence, string MenuType, string UiId);

// WebApplicationFactory<Program> 진입점 노출(스모크 테스트용).
public partial class Program { }
