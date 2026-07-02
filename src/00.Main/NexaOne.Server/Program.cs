using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using NexaOne.ServiceContracts.Ems;
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

    // 실시간 복원 — 루트 Spring 컨텍스트의 messageBus(InMemoryMessageBus)에 SignalR 구독자를 붙여 도메인 이벤트를 UI로 푸시한다.
    // Kafka 모드(messageBus=KafkaMessageBus)는 KafkaConsumerService가 구독 경로를 담당하므로 여기선 인메모리만 배선한다.
    if (server.GetServerBean("messageBus") is NexaOne.Infrastructure.Messaging.InMemoryMessageBus inMemoryBus)
        builder.Services.AddSingleton<IHostedService>(sp =>
            new NexaOne.Server.Realtime.InMemoryBusSubscriberService(inMemoryBus, sp.GetRequiredService<IServiceScopeFactory>()));

    // 복잡 서비스 얇은 브리지(ADR-008) — 모듈 빈을 공유 계약 인터페이스로 캐스트해 DI 등록(CQ-4 헬퍼).
    // 캐스트 실패 = 계약 어셈블리 ALC 동일성 위반(NexaOne.ServiceContracts가 plugin ALC로 복제 로드,
    // 모듈 게시 deps-제외 누락 등) → 기동 시 즉시 폭발(무음 런타임 실패 방지).
    void RegisterBridge<TContract>(string module, string beanName) where TContract : class
    {
        var bridge = server.GetBean(module, beanName) as TContract
            ?? throw new InvalidOperationException(
                $"{beanName} 빈을 {typeof(TContract).Name}로 캐스트하지 못했습니다 — "
                + "NexaOne.ServiceContracts ALC 동일성(ADR-008/모듈 게시 deps-제외) 확인.");
        builder.Services.AddSingleton(bridge);
    }

    RegisterBridge<IEquipmentStateBridge>("Est", "equipmentStateBridge");   // EST 설비상태
    RegisterBridge<IEquipmentAlarmBridge>("Est", "equipmentAlarmBridge");   // EST 설비알람
    RegisterBridge<IRecipeApprovalBridge>("Rms", "rmsRecipeBridge");        // RMS 레시피 승인
    RegisterBridge<IShipmentBridge>("Shp", "shipmentBridge");               // SHP 출하주문 생명주기
    RegisterBridge<IQmsBridge>("Qms", "qmsBridge");                         // QMS 부적합 확정·SPC 관리한계
    // MDM: 첫 미노출-모듈 브리지 부팅 — 캐스트 성공 = plugin ALC가 공유 계약을 Default ALC와 동일 타입으로 보는지 입증.
    RegisterBridge<IMdmEquipmentBridge>("Mdm", "mdmEquipmentBridge");       // MDM 설비 생성/비활성/갱신(불변식)
    RegisterBridge<IMdmMasterBridge>("Mdm", "mdmMasterBridge");             // MDM 마스터(Plant/Area/Product/CodeClass/Code)
    RegisterBridge<IEmsBridge>("Ems", "emsBridge");                         // EMS 보전(작업지시/보전계획/예비품)
    // POM: Lot Mixing(다중 애그리거트)은 브리지에서 제외(UnitOfWork 선결 → MixingPersistAsync로 해소, 트래킹만 노출).
    RegisterBridge<IPomBridge>("Pom", "pomBridge");                         // POM 생산(계획/오더/Lot 추적)
    // SYS 보안 가드(S7): 자격증명/비밀번호/로그인·잠금 해제는 인증 경로 소유, 승인은 ApprovePersistAsync 소유라 브리지 제외.
    RegisterBridge<ISysBridge>("Sys", "sysBridge");                         // SYS 비-자격증명(역할 관리·신청 반려·사용자 비활성)
    // FDC 워커 가드(S8): 실시간 수집/평가·OPC-UA·발생/해제 이력·수집데이터 기록은 워커 소유(ADR-006, REST 비노출).
    RegisterBridge<IFdcBridge>("Fdc", "fdcBridge");                         // FDC 비-실시간 설정(파라미터그룹/알람설정/인터락규칙)
    RegisterBridge<IOeeAggregationBridge>("Est", "oeeAggregationBridge");   // OEE 수동 집계 트리거(EST 소유 IOeeAggregator 위임)
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
// OEE 집계는 EST 모듈 소유(config/modules/est.xml의 oeeAggregationWorker) — modules-ON에서 IHostedService로 자동발견.
// DB 앱 로그(LOG_VIEWER 화면 원천) — 기본 OFF, AppLogging:Db:Enabled=true로만. Warning+ → 유계 채널 → 플러시 워커.
if (builder.Configuration.GetValue("AppLogging:Db:Enabled", false))
{
    var appLogChannel = System.Threading.Channels.Channel.CreateBounded<NexaOne.Server.Logging.AppLogEntry>(
        new System.Threading.Channels.BoundedChannelOptions(1000)
        { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
    builder.Logging.AddProvider(new NexaOne.Server.Logging.DbLoggerProvider(appLogChannel.Writer));
    builder.Services.AddHostedService(sp => new NexaOne.Server.Logging.AppLogFlushWorker(
        appLogChannel.Reader, sp.GetRequiredService<IRuleDispatcher>()));
}
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// ===== 실시간(SignalR) 복원 — 폐기된 NexaOne.API의 /hubs/smartees 경로를 통합 호스트로 이식(회귀 갭). =====
// 허브는 항상 매핑(모듈 OFF에서도 SPA 연결 성공). 도메인 이벤트→푸시 구독자는 모듈 ON 경로에서 배선(아래).
builder.Services.AddSignalR();
builder.Services.AddSingleton<NexaOne.Server.Realtime.IEesHubNotifier, NexaOne.Server.Realtime.EesHubNotifier>();
builder.Services.TryAddSingleton<NexaOne.Common.Telemetry.ActiveUserTracker>();

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
        // SignalR(WebSocket)은 Authorization 헤더를 실을 수 없으므로 /hubs/* 는 access_token 쿼리스트링에서 토큰을 읽는다(SPA createHub 계약).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();
// ADR-003 권한 정책 선언화(CQ-3) — [RequirePermission(...)]의 "perm:{value}" 정책을 동적 구성. 판정은
// ClaimsPrincipalExtensions.HasPermission('*' 와일드카드 포함)로 수동 가드와 1:1 동일 의미(403).
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, NexaOne.Server.Gateway.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, NexaOne.Server.Gateway.PermissionAuthorizationHandler>();

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
        // 점등된 MDM 업무화면(공장/품목/AREA)이 실제 행을 렌더하도록 최소 MDM 마스터 데이터를 시드한다
        // (MDM_PLANT 비었을 때만, idempotent, Dev 전용). 운영(MSSQL)은 본 경로를 타지 않는다.
        SeedDevMasterDataIfEmpty(gwConn);
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
// API 요청 로그(SYSTEM2 REQLOG 화면 원천) — 기본 OFF, RequestLogging:Enabled=true로만 기록. 인증 이후(USER_ID 캡처).
if (builder.Configuration.GetValue("RequestLogging:Enabled", false))
    app.UseMiddleware<NexaOne.Server.Gateway.RequestLogMiddleware>();
// Blazor 폼/컴포넌트 보호용 안티포저리(설계 §5). [ApiController] JSON 엔드포인트(api/v1/*)는 폼 콘텐츠가 아니라
// 영향받지 않는다 — 안티포저리는 form-data/urlencoded 또는 명시 [RequireAntiforgeryToken]에만 적용된다.
app.UseAntiforgery();

app.MapControllers();

// 실시간 SignalR 허브(/hubs/smartees) — SPA(createHub)가 access_token 쿼리로 연결. [Authorize] 허브(위 JwtBearer OnMessageReceived가 쿼리 토큰 처리).
app.MapHub<NexaOne.Server.Realtime.NexaOneEESHub>("/hubs/smartees");

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

// 개발 SQLite 전용 — MDM_PLANT가 비어 있을 때만 점등된 MDM 업무화면(공장/품목/AREA)이 실제 행을 보이도록
// 최소 마스터 데이터를 시드한다(idempotent). 감사 컬럼은 명시값으로 채운다(SQLite엔 GETUTCDATE 기본값 없음).
static void SeedDevMasterDataIfEmpty(string connectionString)
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
    conn.Open();

    using (var count = conn.CreateCommand())
    {
        count.CommandText = "SELECT COUNT(*) FROM MDM_PLANT";
        if (Convert.ToInt64(count.ExecuteScalar() ?? 0L) > 0) return; // 이미 데이터 존재 → 건너뜀
    }

    var now = DateTime.UtcNow.ToString("o");
    void Exec(System.Data.IDbTransaction tx, string sql, params (string, object)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    using var tx = conn.BeginTransaction();
    // 공장 → 구역(FK PLANT_ID) → 품목. 감사 4컬럼(@by/@at)은 모든 표에 공통.
    foreach (var p in new[] {
        ("PLANT01", "서울공장", "수도권 생산거점", "KR", "Asia/Seoul"),
        ("PLANT02", "부산공장", "영남 생산거점", "KR", "Asia/Seoul") })
        Exec(tx, "INSERT INTO MDM_PLANT (PLANT_ID,PLANT_NAME,DESCRIPTION,COUNTRY,TIME_ZONE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@desc,@country,@tz,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", p.Item1), ("@name", p.Item2), ("@desc", p.Item3), ("@country", p.Item4), ("@tz", p.Item5), ("@at", now));

    foreach (var a in new[] {
        ("AREA01", "조립1동", "조립 라인", "PLANT01"),
        ("AREA02", "포장동", "포장 라인", "PLANT01"),
        ("AREA03", "가공동", "가공 라인", "PLANT02") })
        Exec(tx, "INSERT INTO MDM_AREA (AREA_ID,AREA_NAME,DESCRIPTION,PLANT_ID,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@desc,@plant,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", a.Item1), ("@name", a.Item2), ("@desc", a.Item3), ("@plant", a.Item4), ("@at", now));

    foreach (var pr in new[] {
        ("ITEM01", "완제품 A", "출하용 완제품", "FG", "EA"),
        ("ITEM02", "반제품 B", "공정 중간품", "SF", "EA"),
        ("ITEM03", "원자재 C", "투입 원자재", "RM", "KG") })
        Exec(tx, "INSERT INTO MDM_PRODUCT (PRODUCT_ID,PRODUCT_NAME,DESCRIPTION,PRODUCT_TYPE,UNIT,VALID_STATE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@desc,@type,@unit,'Valid','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", pr.Item1), ("@name", pr.Item2), ("@desc", pr.Item3), ("@type", pr.Item4), ("@unit", pr.Item5), ("@at", now));

    // 설비(점등된 설비 관리 화면용). CREATED_BY/UPDATED_BY는 NOT NULL이며 기본값이 없어 명시 필수.
    foreach (var e in new[] {
        ("EQ01", "가공기 1호", "PLANT01", "AREA01", "CNC", "EQC_GENERAL"),
        ("EQ02", "검사기 1호", "PLANT01", "AREA02", "INSPECTION", "EQC_GENERAL"),
        ("EQ03", "조립기 1호", "PLANT02", "AREA03", "ASSEMBLY", "EQC_GENERAL") })
        Exec(tx, "INSERT INTO MDM_EQUIPMENT (EQUIPMENT_ID,EQUIPMENT_NAME,PLANT_ID,AREA_ID,EQUIPMENT_TYPE,EQUIPMENT_CLASS_ID,VALID_STATE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@plant,@area,@type,@cls,'Active','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", e.Item1), ("@name", e.Item2), ("@plant", e.Item3), ("@area", e.Item4), ("@type", e.Item5), ("@cls", e.Item6), ("@at", now));

    // 코드 클래스 → 코드(사유 코드 그룹/사유 코드 화면용). 코드는 FK(CODE_CLASS_ID)로 클래스 선삽입 필요.
    foreach (var c in new[] {
        ("CC_DEFECT", "결함 사유", "결함 발생 사유 코드"),
        ("CC_DOWNTIME", "비가동 사유", "설비 비가동 사유 코드") })
        Exec(tx, "INSERT INTO MDM_CODE_CLASS (CODE_CLASS_ID,CODE_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));

    foreach (var c in new[] {
        ("RC_SCRATCH", "CC_DEFECT", "흠집", 1),
        ("RC_CRACK", "CC_DEFECT", "균열", 2),
        ("RC_PLAN", "CC_DOWNTIME", "계획 정지", 1),
        ("RC_FAULT", "CC_DOWNTIME", "고장 정지", 2) })
        Exec(tx, "INSERT INTO MDM_CODE (CODE_ID,CODE_CLASS_ID,CODE_NAME,SORT_ORDER,VALID_STATE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@cls,@name,@sort,'Valid','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@cls", c.Item2), ("@name", c.Item3), ("@sort", c.Item4), ("@at", now));

    // QMS 검사 규격(검사 SPEC 관리 화면용). NOMINAL/TOLERANCE는 nullable(계량형만 값).
    const string specSql = "INSERT INTO QMS_INSPECTION_SPEC (SPEC_ID,SPEC_NAME,PROCESS_ID,ITEM_NAME,MEASURE_TYPE,NOMINAL_VALUE,TOLERANCE_PLUS,TOLERANCE_MINUS,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                           "VALUES (@id,@name,@proc,@item,@mt,@nom,@tp,@tm,1,'SYSTEM',@at,'SYSTEM',@at)";
    Exec(tx, specSql, ("@id", "SPEC01"), ("@name", "외관 검사"), ("@proc", "PROC_ASSY"), ("@item", "완제품 A"), ("@mt", "Attribute"),
        ("@nom", DBNull.Value), ("@tp", DBNull.Value), ("@tm", DBNull.Value), ("@at", now));
    Exec(tx, specSql, ("@id", "SPEC02"), ("@name", "치수 검사"), ("@proc", "PROC_MACH"), ("@item", "반제품 B"), ("@mt", "Variable"),
        ("@nom", 10.0m), ("@tp", 0.5m), ("@tm", 0.5m), ("@at", now));

    // QMS SPC 파라미터(SPC 관리도 화면용). EQUIPMENT_ID는 위에서 시드한 설비 참조. USL/LSL은 nullable.
    const string spcSql = "INSERT INTO QMS_SPC_PARAM (PARAM_ID,PARAM_NAME,EQUIPMENT_ID,PROCESS_ID,MEAN,UCL,LCL,USL,LSL,SAMPLE_SIZE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                          "VALUES (@id,@name,@eq,@proc,@mean,@ucl,@lcl,@usl,@lsl,@n,1,'SYSTEM',@at,'SYSTEM',@at)";
    Exec(tx, spcSql, ("@id", "SP01"), ("@name", "치수 X"), ("@eq", "EQ01"), ("@proc", "PROC_MACH"),
        ("@mean", 10.0m), ("@ucl", 11.0m), ("@lcl", 9.0m), ("@usl", 11.5m), ("@lsl", 8.5m), ("@n", 5), ("@at", now));
    Exec(tx, spcSql, ("@id", "SP02"), ("@name", "가동 온도"), ("@eq", "EQ03"), ("@proc", "PROC_ASSY"),
        ("@mean", 200.0m), ("@ucl", 210.0m), ("@lcl", 190.0m), ("@usl", DBNull.Value), ("@lsl", DBNull.Value), ("@n", 5), ("@at", now));

    // ===== V035 신설 마스터 시드(점등 화면이 실제 행을 보이도록). FK 순서: 분류 → 본체 → 라우팅/BOM/Qtime. =====
    // 분류 마스터 5종(공통 형태: id/name/desc). 테이블·컬럼명만 달라 개별 루프.
    foreach (var c in new[] { ("EQC_GENERAL", "일반 설비", "범용 설비 그룹"), ("EQC_PRECISION", "정밀 설비", "정밀 가공 설비 그룹") })
        Exec(tx, "INSERT INTO MDM_EQUIPMENT_CLASS (EQUIPMENT_CLASS_ID,EQUIPMENT_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
    foreach (var c in new[] { ("IC_FG", "완제품", "Finished Goods"), ("IC_SF", "반제품", "Semi-Finished"), ("IC_RM", "원자재", "Raw Material") })
        Exec(tx, "INSERT INTO MDM_ITEM_CLASS (ITEM_CLASS_ID,ITEM_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
    foreach (var c in new[] { ("CRC_PLASTIC", "플라스틱 캐리어", "플라스틱 재질"), ("CRC_METAL", "금속 캐리어", "금속 재질") })
        Exec(tx, "INSERT INTO MDM_CARRIER_CLASS (CARRIER_CLASS_ID,CARRIER_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
    foreach (var c in new[] { ("SGC_ASSY", "조립공정", "조립 라인 공정군"), ("SGC_TEST", "검사공정", "검사/시험 공정군") })
        Exec(tx, "INSERT INTO MDM_SEGMENT_CLASS (SEGMENT_CLASS_ID,SEGMENT_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
    foreach (var c in new[] { ("PRC_AUTO", "자동화공정", "자동 설비 공정"), ("PRC_MANUAL", "수동공정", "작업자 수동 공정") })
        Exec(tx, "INSERT INTO MDM_PROCESS_CLASS (PROCESS_CLASS_ID,PROCESS_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));

    // 본체(그룹 참조).
    foreach (var c in new[] { ("CR01", "PC 캐리어", "CRC_PLASTIC", "PC 트레이"), ("CR02", "금속 트레이", "CRC_METAL", "스테인리스 트레이") })
        Exec(tx, "INSERT INTO MDM_CARRIER (CARRIER_ID,CARRIER_NAME,CARRIER_CLASS_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@cls,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@cls", c.Item3), ("@desc", c.Item4), ("@at", now));
    foreach (var c in new[] { ("SEG01", "SMT 조립", "SGC_ASSY", "표면실장 조립"), ("SEG02", "기능 검사", "SGC_TEST", "기능 시험") })
        Exec(tx, "INSERT INTO MDM_SEGMENT (SEGMENT_ID,SEGMENT_NAME,SEGMENT_CLASS_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@cls,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@cls", c.Item3), ("@desc", c.Item4), ("@at", now));
    foreach (var c in new[] { ("PROC01", "자동 투입", "PRC_AUTO", "자동 자재 투입"), ("PROC02", "수동 검사", "PRC_MANUAL", "작업자 육안 검사") })
        Exec(tx, "INSERT INTO MDM_PROCESS (PROCESS_ID,PROCESS_NAME,PROCESS_CLASS_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@cls,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@cls", c.Item3), ("@desc", c.Item4), ("@at", now));

    // 라우팅/BOM(제품 ITEM01~03 참조 — 위에서 시드).
    foreach (var c in new[] { ("RT01", "완제품 A 라우팅", "ITEM01", "표준 라우팅"), ("RT02", "반제품 B 라우팅", "ITEM02", "중간 라우팅") })
        Exec(tx, "INSERT INTO MDM_ROUTING (ROUTING_ID,ROUTING_NAME,PRODUCT_ID,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@prod,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@prod", c.Item3), ("@desc", c.Item4), ("@at", now));
    const string bomSql = "INSERT INTO MDM_BOM (BOM_ID,PRODUCT_ID,COMPONENT_ID,QUANTITY,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@prod,@comp,@qty,@desc,'SYSTEM',@at,'SYSTEM',@at)";
    Exec(tx, bomSql, ("@id", "BOM01"), ("@prod", "ITEM01"), ("@comp", "ITEM03"), ("@qty", 10.0m), ("@desc", "완제품 A ← 원자재 C"), ("@at", now));
    Exec(tx, bomSql, ("@id", "BOM02"), ("@prod", "ITEM01"), ("@comp", "ITEM02"), ("@qty", 2.0m), ("@desc", "완제품 A ← 반제품 B"), ("@at", now));

    // Qtime/Qtime 액션(공정 SEG01/02 참조).
    const string qtSql = "INSERT INTO MDM_QTIME (QTIME_ID,SEGMENT_ID,STANDARD_TIME,UNIT,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@seg,@t,@unit,@desc,'SYSTEM',@at,'SYSTEM',@at)";
    Exec(tx, qtSql, ("@id", "QT01"), ("@seg", "SEG01"), ("@t", 30.0m), ("@unit", "분"), ("@desc", "SMT 조립 표준시간"), ("@at", now));
    Exec(tx, qtSql, ("@id", "QT02"), ("@seg", "SEG02"), ("@t", 60.0m), ("@unit", "분"), ("@desc", "기능 검사 표준시간"), ("@at", now));
    foreach (var c in new[] { ("QA01", "QT01", "ACT_HOLD", "표준시간 초과 보류"), ("QA02", "QT01", "ACT_RELEASE", "검토 후 해제") })
        Exec(tx, "INSERT INTO MDM_QTIME_ACTION (ACTION_ID,QTIME_ID,ACTION_CODE,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@qt,@code,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@qt", c.Item2), ("@code", c.Item3), ("@desc", c.Item4), ("@at", now));

    // ===== V037~V044 신설 QMS 마스터/트랜잭션 시드(점등 화면이 실제 행을 보이도록). 감사·IS_ACTIVE·STATUS는 DDL DEFAULT로 채움. =====
    // 검사항목/검사정의/수입검사방법(기준정보 V037)
    foreach (var c in new[] { ("INSP_VISUAL", "외관 검사", "Incoming", "Attribute"), ("INSP_DIM", "치수 검사", "Process", "Variable"), ("INSP_FUNC", "기능 검사", "Shipping", "Attribute") })
        Exec(tx, "INSERT INTO QMS_INSPECTION_ITEM (ITEM_ID,ITEM_NAME,INSPECTION_TYPE,MEASURE_TYPE,UNIT) VALUES (@id,@name,@t,@mt,'EA')",
            ("@id", c.Item1), ("@name", c.Item2), ("@t", c.Item3), ("@mt", c.Item4));
    foreach (var c in new[] { ("IDEF_IN", "수입검사 정의", "Incoming"), ("IDEF_PR", "공정검사 정의", "Process") })
        Exec(tx, "INSERT INTO QMS_INSPECTION_DEF (INSP_DEF_ID,INSP_DEF_NAME,PROCESS_ID,PRODUCT_ID,INSPECTION_TYPE) VALUES (@id,@name,'PROC01','ITEM03',@t)",
            ("@id", c.Item1), ("@name", c.Item2), ("@t", c.Item3));
    foreach (var c in new[] { ("IM_AQL10", "AQL 1.0 정상검사", "AQL", "1.0"), ("IM_FULL", "전수검사", "Full", "-") })
        Exec(tx, "INSERT INTO QMS_INCOMING_INSP_METHOD (METHOD_ID,METHOD_NAME,PRODUCT_ID,SAMPLING_TYPE,AQL_LEVEL) VALUES (@id,@name,'ITEM03',@st,@aql)",
            ("@id", c.Item1), ("@name", c.Item2), ("@st", c.Item3), ("@aql", c.Item4));

    // 검사 실행(수입/공정/출하)
    foreach (var c in new[] { ("INS_IN1", "Incoming", "LOT_IN_001", "ITEM03", "EQ02", "Pass", 0), ("INS_PR1", "Process", "LOT_PR_001", "ITEM02", "EQ01", "Pass", 0), ("INS_SH1", "Shipping", "LOT_SH_001", "ITEM01", "EQ02", "Fail", 2) })
        Exec(tx, "INSERT INTO QMS_INSPECTION (INSPECTION_ID,INSPECTION_TYPE,LOT_ID,PRODUCT_ID,EQUIPMENT_ID,SPEC_ID,INSPECTED_AT,INSPECTOR_ID,RESULT,SAMPLE_QTY,DEFECT_QTY,IS_CONFIRMED) " +
                 "VALUES (@id,@t,@lot,@prod,@eq,'SPEC01',@at,'admin',@r,10,@d,1)",
            ("@id", c.Item1), ("@t", c.Item2), ("@lot", c.Item3), ("@prod", c.Item4), ("@eq", c.Item5), ("@at", now), ("@r", c.Item6), ("@d", c.Item7));

    // 장기재고검사(자재/제품)
    foreach (var c in new[] { ("LT_MAT1", "Material", "ITEM03", "Completed"), ("LT_PRD1", "Product", "ITEM01", "Requested") })
        Exec(tx, "INSERT INTO QMS_LONGTERM_INSPECTION (LT_INSP_ID,TARGET_TYPE,PRODUCT_ID,LOT_ID,WAREHOUSE,REQUEST_DATE,REQUESTED_BY,STATUS) " +
                 "VALUES (@id,@t,@prod,'LOT_LT_01','창고A',@at,'admin',@st)",
            ("@id", c.Item1), ("@t", c.Item2), ("@prod", c.Item3), ("@at", now), ("@st", c.Item4));

    // 클레임
    foreach (var c in new[] { ("CLM001", "CL-2026-001", "현대전자", "ITEM01", "Quality", "Critical", "Received"), ("CLM002", "CL-2026-002", "삼성SDI", "ITEM02", "Delivery", "Major", "Completed") })
        Exec(tx, "INSERT INTO QMS_CLAIM (CLAIM_ID,CLAIM_NO,CUSTOMER_NAME,PRODUCT_ID,CLAIM_TYPE,OCCURRED_DATE,SEVERITY,STATUS,ASSIGNEE_ID) " +
                 "VALUES (@id,@no,@cust,@prod,@ct,@at,@sv,@st,'admin')",
            ("@id", c.Item1), ("@no", c.Item2), ("@cust", c.Item3), ("@prod", c.Item4), ("@ct", c.Item5), ("@at", now), ("@sv", c.Item6), ("@st", c.Item7));

    // NCR
    foreach (var c in new[] { ("NCR001", "NCR-2026-001", "Process", "LOT_PR_001", "Open"), ("NCR002", "NCR-2026-002", "Incoming", "LOT_IN_001", "Closed") })
        Exec(tx, "INSERT INTO QMS_NCR (NCR_ID,NCR_NO,SOURCE_TYPE,LOT_ID,PRODUCT_ID,ISSUED_DATE,ISSUED_BY,DISPOSITION,STATUS) " +
                 "VALUES (@id,@no,@src,@lot,'ITEM02',@at,'admin','Rework',@st)",
            ("@id", c.Item1), ("@no", c.Item2), ("@src", c.Item3), ("@lot", c.Item4), ("@at", now), ("@st", c.Item5));

    // Hold/Release · 4M 변경
    Exec(tx, "INSERT INTO QMS_HOLD_RELEASE (HOLD_ID,LOT_ID,PRODUCT_ID,HOLD_TYPE,RISK_RANGE,REASON,REQUESTED_BY,REQUESTED_AT,STATUS) " +
             "VALUES ('HOLD001','LOT_SH_001','ITEM01','Hold','High','출하검사 부적합 보류','admin',@at,'Hold')", ("@at", now));
    Exec(tx, "INSERT INTO QMS_4M_CHANGE (CHANGE_ID,CHANGE_NO,CHANGE_TYPE,EQUIPMENT_ID,PRODUCT_ID,CHANGE_DATE,DESCRIPTION,REQUESTED_BY,APPROVAL_STATUS) " +
             "VALUES ('4M001','4M-2026-001','Machine','EQ01','ITEM01',@at,'가공기 1호 공구 교체','admin','Approved')", ("@at", now));

    // 계측기 + 검교정/RNR/수리
    foreach (var c in new[] { ("GA01", "버니어캘리퍼스", "측정", "CD-15CP"), ("GA02", "마이크로미터", "측정", "MDC-25MX") })
        Exec(tx, "INSERT INTO QMS_GAUGE (GAUGE_ID,GAUGE_NAME,GAUGE_TYPE,MODEL,SERIAL_NO,LOCATION,EQUIPMENT_ID,CALIBRATION_CYCLE_DAYS,NEXT_CALIBRATION_AT) " +
                 "VALUES (@id,@name,@t,@model,@id,'검사실','EQ02',365,@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@t", c.Item3), ("@model", c.Item4), ("@at", now));
    Exec(tx, "INSERT INTO QMS_GAUGE_CALIBRATION_PLAN (PLAN_ID,GAUGE_ID,PLAN_NAME,SCHEDULED_DATE,CYCLE_TYPE,ASSIGNEE_ID,STATUS) VALUES ('CP01','GA01','연간 검교정',@at,'Annual','admin','Planned')", ("@at", now));
    Exec(tx, "INSERT INTO QMS_GAUGE_CALIBRATION_RESULT (RESULT_ID,GAUGE_ID,PLAN_ID,CALIBRATED_AT,CALIBRATED_BY,RESULT,CERTIFICATE_NO) VALUES ('CR01','GA01','CP01',@at,'한국인정','Pass','CERT-2026-001')", ("@at", now));
    Exec(tx, "INSERT INTO QMS_GAUGE_RNR_PLAN (RNR_PLAN_ID,GAUGE_ID,PLAN_NAME,SCHEDULED_DATE,OPERATOR_COUNT,TRIAL_COUNT,PART_COUNT,STATUS) VALUES ('RP01','GA01','버니어 R&R',@at,3,2,10,'Planned')", ("@at", now));
    Exec(tx, "INSERT INTO QMS_GAUGE_RNR_RESULT (RNR_RESULT_ID,RNR_PLAN_ID,GAUGE_ID,EVALUATED_AT,EVALUATED_BY,GAGE_RR_PERCENT,NDC,JUDGEMENT) VALUES ('RR01','RP01','GA01',@at,'admin',8.5,12,'Accept')", ("@at", now));
    Exec(tx, "INSERT INTO QMS_GAUGE_REPAIR_RESULT (REPAIR_ID,GAUGE_ID,REPAIRED_AT,REPAIRED_BY,FAILURE_DESC,REPAIR_DESC,COST) VALUES ('RE01','GA02',@at,'외주','영점 불량','영점 조정',50000)", ("@at", now));

    // 협력사 평가(항목→정의→연결→실적→시정조치)
    foreach (var c in new[] { ("SI_Q", "품질", "Quality", 40), ("SI_D", "납기", "Delivery", 30), ("SI_P", "가격", "Price", 30) })
        Exec(tx, "INSERT INTO QMS_SPM_EVAL_ITEM (ITEM_ID,ITEM_NAME,CATEGORY,MAX_SCORE) VALUES (@id,@name,@cat,@max)",
            ("@id", c.Item1), ("@name", c.Item2), ("@cat", c.Item3), ("@max", c.Item4));
    Exec(tx, "INSERT INTO QMS_SPM_EVAL_DEF (DEF_ID,DEF_NAME,EVAL_CYCLE,TARGET_TYPE) VALUES ('SD_ANN','연간 정기평가','Annual','Supplier')");
    foreach (var c in new[] { ("SP_Q", "SI_Q", 40, 1), ("SP_D", "SI_D", 30, 2), ("SP_P", "SI_P", 30, 3) })
        Exec(tx, "INSERT INTO QMS_SPM_EVAL_PARAM (PARAM_ID,DEF_ID,ITEM_ID,WEIGHT,SORT_ORDER) VALUES (@id,'SD_ANN',@item,@w,@o)",
            ("@id", c.Item1), ("@item", c.Item2), ("@w", c.Item3), ("@o", c.Item4));
    foreach (var c in new[] { ("SR01", "SUP_A", "대한정밀", "A", 92.5m), ("SR02", "SUP_B", "한일소재", "B", 78.0m) })
        Exec(tx, "INSERT INTO QMS_SPM_EVAL_RESULT (RESULT_ID,SUPPLIER_ID,SUPPLIER_NAME,DEF_ID,EVAL_PERIOD,TOTAL_SCORE,GRADE,EVALUATED_AT,EVALUATOR_ID) " +
                 "VALUES (@id,@sid,@sname,'SD_ANN','2026',@score,@grade,@at,'admin')",
            ("@id", c.Item1), ("@sid", c.Item2), ("@sname", c.Item3), ("@grade", c.Item4), ("@score", c.Item5), ("@at", now));
    Exec(tx, "INSERT INTO QMS_SPM_ACTION_RESULT (ACTION_ID,RESULT_ID,SUPPLIER_ID,ACTION_DESC,ACTION_DATE,STATUS) VALUES ('AR01','SR02','SUP_B','납기 개선 시정조치',@at,'Open')", ("@at", now));

    // ===== EMS(설비보전) 시드 — 예비품(V027)/그룹·입출고(V045)/작업지시(V008)/보전계획(V027). V008/V027 감사 컬럼은 DEFAULT가 없어 명시 필수. =====
    const string emsPartSql = "INSERT INTO EMS_SPARE_PART (PART_ID,PART_NAME,PART_NUMBER,DESCRIPTION,UNIT_OF_MEASURE,CURRENT_STOCK,MIN_STOCK,MAX_STOCK,LOCATION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@no,@desc,@uom,@cur,@min,@max,@loc,'SYSTEM',@at,'SYSTEM',@at)";
    Exec(tx, emsPartSql, ("@id", "ESP01"), ("@name", "베어링 6204"), ("@no", "BRG-6204"), ("@desc", "회전부 베어링"), ("@uom", "EA"), ("@cur", 50), ("@min", 10), ("@max", 100), ("@loc", "자재창고 A"), ("@at", now));
    Exec(tx, emsPartSql, ("@id", "ESP02"), ("@name", "모터 1.5kW"), ("@no", "MTR-15"), ("@desc", "구동 모터"), ("@uom", "EA"), ("@cur", 8), ("@min", 5), ("@max", 20), ("@loc", "자재창고 B"), ("@at", now));
    Exec(tx, emsPartSql, ("@id", "ESP03"), ("@name", "근접센서"), ("@no", "SNS-PRX"), ("@desc", "감지 센서"), ("@uom", "EA"), ("@cur", 30), ("@min", 10), ("@max", 60), ("@loc", "자재창고 A"), ("@at", now));
    foreach (var c in new[] { ("ESPC_BRG", "베어링류", "회전부 베어링"), ("ESPC_MTR", "모터류", "구동 모터") })
        Exec(tx, "INSERT INTO EMS_SPARE_PART_CLASS (PART_CLASS_ID,PART_CLASS_NAME,DESCRIPTION,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@desc,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@desc", c.Item3), ("@at", now));
    foreach (var c in new[] { ("EIO01", "ESP01", "Incoming", 20, "입고처", "자재창고 A"), ("EIO02", "ESP02", "Move", 2, "자재창고 B", "조립1동"), ("EIO03", "ESP03", "Scrap", 5, "자재창고 A", "폐기장") })
        Exec(tx, "INSERT INTO EMS_SPARE_PART_INOUT (INOUT_ID,PART_ID,TRANSACTION_TYPE,QUANTITY,FROM_LOCATION,TO_LOCATION,TRANSACTION_AT,PROCESSED_BY,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@pid,@t,@q,@from,@to,@at,'admin','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@pid", c.Item2), ("@t", c.Item3), ("@q", c.Item4), ("@from", c.Item5), ("@to", c.Item6), ("@at", now));
    foreach (var c in new[] { ("EWO01", "EQ01", "BM", "가공기 1호 베어링 교체"), ("EWO02", "EQ02", "PM", "검사기 1호 정기점검") })
        Exec(tx, "INSERT INTO EMS_WORK_ORDER (WO_ID,EQUIPMENT_ID,WO_TYPE,DESCRIPTION,ASSIGNEE_ID,ISSUED_AT,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@eq,@t,@desc,'admin',@at,'Issued','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@eq", c.Item2), ("@t", c.Item3), ("@desc", c.Item4), ("@at", now));
    foreach (var c in new[] { ("EMP01", "월간 정기점검", "EQ01", "PM", "Monthly"), ("EMP02", "분기 정밀점검", "EQ03", "PM", "Quarterly") })
        Exec(tx, "INSERT INTO EMS_MAINTENANCE_PLAN (PLAN_ID,PLAN_NAME,EQUIPMENT_ID,PLAN_TYPE,CYCLE_TYPE,SCHEDULED_DATE,ESTIMATED_DURATION_HOURS,ASSIGNEE_ID,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@eq,@pt,@ct,@at,2.0,'admin','Planned','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", c.Item1), ("@name", c.Item2), ("@eq", c.Item3), ("@pt", c.Item4), ("@ct", c.Item5), ("@at", now));

    // ===== EST OEE(설비종합효율) 시드(V050) — 점등된 OEE/유실/지표 화면이 실제 값을 보이도록. EQ01~03 참조(FK).
    // 비율(가용성/성능/품질/OEE)은 분율(0~1)로 저장하고 값은 사전집계 예시다(원자료→마트 집계는 배치/워커 소관). =====
    var yesterday = DateTime.UtcNow.AddDays(-1).ToString("o");
    const string oeeSql = "INSERT INTO EST_OEE_SUMMARY (OEE_ID,PLANT_ID,EQUIPMENT_ID,OEE_DATE,SHIFT_ID,PLANNED_MINUTES,DOWNTIME_MINUTES,OPERATING_MINUTES,IDEAL_CYCLE_TIME_SEC,TOTAL_COUNT,GOOD_COUNT,DEFECT_COUNT,AVAILABILITY,PERFORMANCE,QUALITY,OEE) " +
        "VALUES (@id,@plant,@eq,@date,@shift,@pm,@dm,@om,@ict,@tc,@gc,@dc,@av,@pf,@ql,@oee)";
    Exec(tx, oeeSql, ("@id", "OEE01"), ("@plant", "PLANT01"), ("@eq", "EQ01"), ("@date", now), ("@shift", "SHIFT_D"), ("@pm", 480m), ("@dm", 60m), ("@om", 420m), ("@ict", 30m), ("@tc", 800m), ("@gc", 780m), ("@dc", 20m), ("@av", 0.8750m), ("@pf", 0.9520m), ("@ql", 0.9750m), ("@oee", 0.8120m));
    Exec(tx, oeeSql, ("@id", "OEE02"), ("@plant", "PLANT01"), ("@eq", "EQ01"), ("@date", yesterday), ("@shift", "SHIFT_N"), ("@pm", 480m), ("@dm", 90m), ("@om", 390m), ("@ict", 30m), ("@tc", 760m), ("@gc", 740m), ("@dc", 20m), ("@av", 0.8125m), ("@pf", 0.9740m), ("@ql", 0.9737m), ("@oee", 0.7706m));
    Exec(tx, oeeSql, ("@id", "OEE03"), ("@plant", "PLANT01"), ("@eq", "EQ02"), ("@date", now), ("@shift", "SHIFT_D"), ("@pm", 480m), ("@dm", 120m), ("@om", 360m), ("@ict", 40m), ("@tc", 500m), ("@gc", 470m), ("@dc", 30m), ("@av", 0.7500m), ("@pf", 0.9259m), ("@ql", 0.9400m), ("@oee", 0.6528m));
    Exec(tx, oeeSql, ("@id", "OEE04"), ("@plant", "PLANT02"), ("@eq", "EQ03"), ("@date", now), ("@shift", "SHIFT_D"), ("@pm", 480m), ("@dm", 30m), ("@om", 450m), ("@ict", 25m), ("@tc", 1000m), ("@gc", 990m), ("@dc", 10m), ("@av", 0.9375m), ("@pf", 0.9259m), ("@ql", 0.9900m), ("@oee", 0.8594m));

    // 유실 상세(6대 손실) — WORST5 유실: EQ02(115분) > EQ01(65) > EQ03(30). LOSS_CODE는 느슨 참조(FK 없음).
    const string lossSql = "INSERT INTO EST_OEE_LOSS (LOSS_ID,PLANT_ID,EQUIPMENT_ID,OEE_DATE,SHIFT_ID,LOSS_CATEGORY,LOSS_CODE,LOSS_NAME,LOSS_MINUTES,OCCURRED_AT,REASON) " +
        "VALUES (@id,@plant,@eq,@date,'SHIFT_D',@cat,@code,@name,@min,@at,@reason)";
    foreach (var l in new[] {
        ("LOSS01", "PLANT01", "EQ01", "Breakdown", "RC_FAULT",  "고장 정지", 45m, "베어링 파손"),
        ("LOSS02", "PLANT01", "EQ01", "Setup",     "RC_PLAN",   "계획 정지", 20m, "금형 교체"),
        ("LOSS03", "PLANT01", "EQ02", "Breakdown", "RC_FAULT",  "고장 정지", 90m, "모터 과열"),
        ("LOSS04", "PLANT01", "EQ02", "MinorStop", "RC_MINOR",  "순간 정지", 15m, "자재 걸림"),
        ("LOSS05", "PLANT01", "EQ02", "Defect",    "RC_SCRATCH","불량 손실", 10m, "흠집 다발"),
        ("LOSS06", "PLANT02", "EQ03", "Setup",     "RC_PLAN",   "계획 정지", 25m, "셋업 조정"),
        ("LOSS07", "PLANT02", "EQ03", "SpeedLoss", "RC_SPEED",  "속도 저하",  5m, "저속 운전") })
        Exec(tx, lossSql, ("@id", l.Item1), ("@plant", l.Item2), ("@eq", l.Item3), ("@date", now), ("@cat", l.Item4), ("@code", l.Item5), ("@name", l.Item6), ("@min", l.Item7), ("@at", now), ("@reason", l.Item8));

    // EPT 관심지표 마스터 + 값(지표 관리/관심지표 등록·조회 화면).
    foreach (var i in new[] {
        ("IDX_MTBF", "평균고장간격(MTBF)", "신뢰성", "시간", "고장 간 평균 가동시간"),
        ("IDX_MTTR", "평균수리시간(MTTR)", "보전성", "시간", "고장 1건당 평균 수리시간"),
        ("IDX_UPTIME", "설비 가동률", "가동", "%", "계획 대비 가동시간 비율") })
        Exec(tx, "INSERT INTO EST_EPT_INDEX (INDEX_ID,INDEX_NAME,INDEX_CATEGORY,UNIT,DESCRIPTION,IS_ACTIVE) VALUES (@id,@name,@cat,@unit,@desc,1)",
            ("@id", i.Item1), ("@name", i.Item2), ("@cat", i.Item3), ("@unit", i.Item4), ("@desc", i.Item5));
    const string ivSql = "INSERT INTO EST_EPT_INDEX_VALUE (VALUE_ID,INDEX_ID,EQUIPMENT_ID,PLANT_ID,OEE_DATE,SHIFT_ID,INDEX_VALUE) VALUES (@id,@idx,@eq,@plant,@date,'SHIFT_D',@val)";
    foreach (var v in new[] {
        ("IV01", "IDX_MTBF", "EQ01", "PLANT01", 120.5m),
        ("IV02", "IDX_MTTR", "EQ01", "PLANT01", 2.5m),
        ("IV03", "IDX_UPTIME", "EQ03", "PLANT02", 93.75m) })
        Exec(tx, ivSql, ("@id", v.Item1), ("@idx", v.Item2), ("@eq", v.Item3), ("@plant", v.Item4), ("@date", now), ("@val", v.Item5));

    // ===== OEE 집계 워커 설정(V051) — 상태 분류 + 설비 목표. 워커가 켜지면 원자료를 이 설정과 결합해 마트를 계산한다. =====
    // 작업조(MDM_SHIFT, V046) — OEE 작업조 단위 윈도/계획시간 근거. DAY 08:00~20:00, NIGHT 20:00~08:00(야간 교대).
    foreach (var sh in new[] { ("DAY", "주간조", "08:00", "20:00"), ("NIGHT", "야간조", "20:00", "08:00") })
        Exec(tx, "INSERT INTO MDM_SHIFT (SHIFT_ID,SHIFT_NAME,START_TIME,END_TIME,DESCRIPTION,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@start,@end,@name,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", sh.Item1), ("@name", sh.Item2), ("@start", sh.Item3), ("@end", sh.Item4), ("@at", now));
    // 상태 분류: RUN=가동, DOWN/SETUP/MINOR=비가동(계획 포함), IDLE=비계획(계획시간 제외).
    foreach (var s in new[] {
        ("RUN", "가동", "Productive", 1, 0, 1),
        ("DOWN", "고장 정지", "Breakdown", 0, 1, 1),
        ("SETUP", "셋업/교체", "Setup", 0, 1, 1),
        ("MINOR", "순간 정지", "MinorStop", 0, 1, 1),
        ("IDLE", "비계획 대기", "Idle", 0, 0, 0) })
        Exec(tx, "INSERT INTO EST_STATE_CATEGORY (STATE_ID,STATE_NAME,CATEGORY,IS_PRODUCTIVE,IS_DOWNTIME,IS_SCHEDULED,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@cat,@prod,@down,@sched,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", s.Item1), ("@name", s.Item2), ("@cat", s.Item3), ("@prod", s.Item4), ("@down", s.Item5), ("@sched", s.Item6), ("@at", now));
    foreach (var t in new[] {
        ("EQ01", 30m, 480m, "가공기 1호 목표(30초/개)"),
        ("EQ02", 40m, 480m, "검사기 1호 목표(40초/개)"),
        ("EQ03", 25m, 480m, "조립기 1호 목표(25초/개)") })
        Exec(tx, "INSERT INTO EST_OEE_TARGET (EQUIPMENT_ID,IDEAL_CYCLE_TIME_SEC,PLANNED_MINUTES,DESCRIPTION,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@eq,@ict,@pm,@desc,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@eq", t.Item1), ("@ict", t.Item2), ("@pm", t.Item3), ("@desc", t.Item4), ("@at", now));

    // POM_LOT(WPM 작업진행/LOT추적/수율 화면용) — 홀드·불량 섞어 시드. ROUTE_STEPS/CREATED_BY NOT NULL.
    foreach (var l in new[] {
        ("LOT01", "PLANT01", "ITEM01", 100m, 5m, "Processing", "N"),
        ("LOT02", "PLANT01", "ITEM01", 200m, 0m, "Completed", "N"),
        ("LOT03", "PLANT02", "ITEM02", 150m, 12m, "Processing", "Y") })
        Exec(tx, "INSERT INTO POM_LOT (LOT_ID,PLANT_ID,PRODUCT_ID,QTY,DEFECT_QTY,LOT_STATE,PROCESS_STATE,ROUTE_STEPS,CURRENT_STEP,IS_HOLD,CREATED_BY,CREATED_AT) " +
                 "VALUES (@id,@plant,@prod,@qty,@def,@st,'Idle','투입>가공>검사',1,@hold,'SYSTEM',@at)",
            ("@id", l.Item1), ("@plant", l.Item2), ("@prod", l.Item3), ("@qty", l.Item4), ("@def", l.Item5), ("@st", l.Item6), ("@hold", l.Item7), ("@at", now));
    // POM_LOT_HISTORY(LOT 추적 화면용) — LOT_HISTORY_ID는 IDENTITY(자동).
    foreach (var h in new[] { ("PLANT01", "LOT01", "EQ01", "TrackIn"), ("PLANT01", "LOT02", "EQ02", "TrackOut") })
        Exec(tx, "INSERT INTO POM_LOT_HISTORY (PLANT_ID,LOT_ID,EQUIPMENT_ID,PROCESS_ID,TRACK_IN_TIME,EXECUTION_ID,EXECUTION_USER,QTY,DEFECT_QTY,LOT_STATE,PROCESS_STATE) " +
                 "VALUES (@plant,@lot,@eq,'PROC_MACH',@at,@exec,'admin',100,0,'Processing','Run')",
            ("@plant", h.Item1), ("@lot", h.Item2), ("@eq", h.Item3), ("@exec", h.Item4), ("@at", now));

    // PRC_PURCHASE_ORDER(구매오더 관리/현황 화면용, V052) — 발주 헤더 시드.
    foreach (var po in new[] {
        ("PO01", "PLANT01", "원자재 발주", "VEN_A", 500m, "Ordered"),
        ("PO02", "PLANT01", "부자재 발주", "VEN_B", 300m, "Draft"),
        ("PO03", "PLANT02", "소모품 발주", "VEN_A", 120m, "Incoming") })
        Exec(tx, "INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID,PLANT_ID,PURCHASE_ORDER_NAME,VENDOR_ID,ORDER_DATE,ORDER_QTY,OWNER_ID,STATUS,IS_HOLD,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@plant,@name,@vendor,@at,@qty,'admin',@st,'N','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", po.Item1), ("@plant", po.Item2), ("@name", po.Item3), ("@vendor", po.Item4), ("@qty", po.Item5), ("@st", po.Item6), ("@at", now));

    // SLS_SALES_ORDER/REQUEST(판매 오더/요청 화면용, V053) — 헤더+요청 시드.
    foreach (var so in new[] {
        ("SO01", "PLANT01", "완제품 A 판매", "CUST_X", 1000m, "Confirmed"),
        ("SO02", "PLANT01", "완제품 A 추가", "CUST_Y", 500m, "Draft") })
        Exec(tx, "INSERT INTO SLS_SALES_ORDER (SALES_ORDER_ID,PLANT_ID,SALES_ORDER_NAME,CUSTOMER_ID,PRODUCT_ID,PLAN_START_DATE,PLAN_QTY,DELIVERED_QTY,OWNER_ID,STATUS,IS_HOLD,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@plant,@name,@cust,'ITEM01',@at,@qty,0,'admin',@st,'N','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", so.Item1), ("@plant", so.Item2), ("@name", so.Item3), ("@cust", so.Item4), ("@qty", so.Item5), ("@st", so.Item6), ("@at", now));
    foreach (var sr in new[] { ("SR01", "SO01", 400m, "Confirmed"), ("SR02", "SO01", 600m, "Draft") })
        Exec(tx, "INSERT INTO SLS_SALES_REQUEST (SALES_REQUEST_ID,SALES_REQUEST_NAME,SALES_ORDER_ID,CUSTOMER_ID,PRODUCT_ID,REQUEST_DATE,REQUEST_QTY,STATUS,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,'판매 요청',@so,'CUST_X','ITEM01',@at,@qty,@st,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", sr.Item1), ("@so", sr.Item2), ("@qty", sr.Item3), ("@st", sr.Item4), ("@at", now));

    // MDM_LABEL*(FACTORY_STD 라벨 마스터/발행/매핑 화면용, V054). FK: 발행/매핑 → 라벨(선삽입).
    foreach (var lb in new[] { ("LBL01", "제품 라벨"), ("LBL02", "박스 라벨") })
        Exec(tx, "INSERT INTO MDM_LABEL (LABEL_ID,PLANT_ID,LABEL_NAME,DESCRIPTION,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,'PLANT01',@name,@name,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", lb.Item1), ("@name", lb.Item2), ("@at", now));
    foreach (var iss in new[] { ("LIS01", "LBL01", "LOT01", "SN0001", 2), ("LIS02", "LBL01", "LOT02", "SN0002", 1) })
        Exec(tx, "INSERT INTO MDM_LABEL_ISSUE (ISSUE_ID,PLANT_ID,LABEL_ID,ITEM_ID,LOT_ID,SERIAL_NUM,PRINT_CNT,ISSUED_AT,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,'PLANT01',@label,'ITEM01',@lot,@sn,@cnt,@at,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", iss.Item1), ("@label", iss.Item2), ("@lot", iss.Item3), ("@sn", iss.Item4), ("@cnt", iss.Item5), ("@at", now));
    foreach (var mp in new[] { ("LMP01", "LBL01", "PROC_MACH"), ("LMP02", "LBL02", "PROC_ASSY") })
        Exec(tx, "INSERT INTO MDM_LABEL_MAPPING (MAPPING_ID,PLANT_ID,PROCESS_ID,ITEM_ID,LABEL_ID,PRINT_LIMIT_CNT,PRINT_LIMIT_YN,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,'PLANT01',@proc,'ITEM01',@label,5,'Y','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", mp.Item1), ("@label", mp.Item2), ("@proc", mp.Item3), ("@at", now));

    // EST_EPT_LAYOUT/EQUIPMENT_PROPERTY(EPT_STD 레이아웃/속성 화면용, V055). 속성 FK: EQ01~03(선삽입됨).
    foreach (var lo in new[] { ("LAYOUT01", "PLANT01", "조립1동 레이아웃", "AREA01"), ("LAYOUT02", "PLANT02", "가공동 레이아웃", "AREA03") })
        Exec(tx, "INSERT INTO EST_EPT_LAYOUT (LAYOUT_ID,PLANT_ID,LAYOUT_NAME,AREA_ID,WIDTH,HEIGHT,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@plant,@name,@area,1024,768,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", lo.Item1), ("@plant", lo.Item2), ("@name", lo.Item3), ("@area", lo.Item4), ("@at", now));
    foreach (var pr in new[] { ("EQ01", "PLANT01", 30m), ("EQ02", "PLANT01", 40m), ("EQ03", "PLANT02", 25m) })
        Exec(tx, "INSERT INTO EST_EPT_EQUIPMENT_PROPERTY (EQUIPMENT_ID,PLANT_ID,DESCRIPTION,CYCLE_TIME,DO_ALARM_INTERLOCK,DO_MCC,DO_SUMMARY,DO_TACT_TIME,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@eq,@plant,'설비 EPT 속성',@ct,'Y','Y','Y','Y',1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@eq", pr.Item1), ("@plant", pr.Item2), ("@ct", pr.Item3), ("@at", now));

    // MICUBE→EST(설비상태 표준) 시드 — 상태매트릭스(V025) + 이벤트/알람상태/이벤트상태 매핑(V056).
    foreach (var m in new[] { ("PLANT01", "IDLE", "RUN"), ("PLANT01", "RUN", "DOWN"), ("PLANT02", "RUN", "IDLE") })
        Exec(tx, "INSERT INTO EST_STATE_MATRIX (PLANT_ID,FROM_STATE_ID,TO_STATE_ID,ALLOW_FLAG,SET_STATE_ID,REQUIRE_REASON,VALID_STATE) VALUES (@p,@from,@to,'Y',@to,'N','Valid')",
            ("@p", m.Item1), ("@from", m.Item2), ("@to", m.Item3));
    foreach (var ev in new[] { ("EV01", "도어 열림", "EQ01", "Safety"), ("EV02", "비상정지", "EQ02", "Safety") })
        Exec(tx, "INSERT INTO EST_EQUIPMENT_EVENT (EVENT_ID,PLANT_ID,EVENT_NAME,EQUIPMENT_ID,EVENT_TYPE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,'PLANT01',@name,@eq,@type,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", ev.Item1), ("@name", ev.Item2), ("@eq", ev.Item3), ("@type", ev.Item4), ("@at", now));
    Exec(tx, "INSERT INTO EST_STATE_ALARM_MAP (MAP_ID,PLANT_ID,EQUIPMENT_ID,ALARM_DEF_ID,SET_STATE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES ('SAM01','PLANT01','EQ01','ALM_OVERHEAT','DOWN',1,'SYSTEM',@at,'SYSTEM',@at)", ("@at", now));
    Exec(tx, "INSERT INTO EST_STATE_EVENT_MAP (MAP_ID,PLANT_ID,EQUIPMENT_ID,EVENT_ID,SET_STATE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES ('SEM01','PLANT01','EQ01','EV01','IDLE',1,'SYSTEM',@at,'SYSTEM',@at)", ("@at", now));

    // MICUBE→COM(알람메일 알림) 시드 — 메일서버/수신자(일반·알람)/서비스(V057).
    Exec(tx, "INSERT INTO COM_MAIL_SERVER (SERVER_ID,SERVER_NAME,HOST,PORT,SENDER_ADDRESS,USE_SSL,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES ('SMTP01','기본 SMTP','smtp.factory.local',587,'noreply@factory.local','Y',1,'SYSTEM',@at,'SYSTEM',@at)", ("@at", now));
    foreach (var rc in new[] { ("RC01", "admin", "EQ01", "Alarm"), ("RC02", "admin", "EQ02", "Mail") })
        Exec(tx, "INSERT INTO COM_MAIL_RECIPIENT (RECIPIENT_ID,PLANT_ID,USER_ID,EQUIPMENT_ID,MAIL_ADDRESS,MAIL_TYPE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,'PLANT01',@user,@eq,'admin@factory.local',@type,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", rc.Item1), ("@user", rc.Item2), ("@eq", rc.Item3), ("@type", rc.Item4), ("@at", now));
    foreach (var sv in new[] { ("SVC01", "알람 수집 서비스", "Collector", "Running"), ("SVC02", "메일 발송 서비스", "Mailer", "Stopped") })
        Exec(tx, "INSERT INTO COM_SERVICE (SERVICE_ID,SERVICE_NAME,SERVICE_TYPE,STATUS,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@name,@type,@st,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", sv.Item1), ("@name", sv.Item2), ("@type", sv.Item3), ("@st", sv.Item4), ("@at", now));

    // MDM_BOR/RESOURCE(FACTORY_STD BOR 화면용, V058). 자원 FK: BOR 선삽입.
    foreach (var b in new[] { ("BOR01", "PLANT01", "조립 BOR", "Condition"), ("BOR02", "PLANT01", "가공 BOR", "Resource") })
        Exec(tx, "INSERT INTO MDM_BOR (BOR_ID,PLANT_ID,BOR_NAME,PROCESS_ID,PRODUCT_ID,BOR_TYPE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@plant,@name,'PROC_ASSY','ITEM01',@type,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", b.Item1), ("@plant", b.Item2), ("@name", b.Item3), ("@type", b.Item4), ("@at", now));
    foreach (var r in new[] { ("BRS01", "BOR01", "Equipment", "EQ01", "가공기 1호", 1m), ("BRS02", "BOR02", "Tool", "TOOL01", "지그 A", 2m) })
        Exec(tx, "INSERT INTO MDM_BOR_RESOURCE (RESOURCE_ID,BOR_ID,RESOURCE_TYPE,RESOURCE_REF_ID,RESOURCE_NAME,REQUIRED_QTY,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) VALUES (@id,@bor,@type,@ref,@name,@qty,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", r.Item1), ("@bor", r.Item2), ("@type", r.Item3), ("@ref", r.Item4), ("@name", r.Item5), ("@qty", r.Item6), ("@at", now));
    // IVT_MATERIAL_TX 이동(이동오더 현황 화면용) — TX_TYPE='Move'.
    foreach (var m in new[] { ("MTX01", "LOT01", "ITEM03", 50m, "자재창고", "조립1동"), ("MTX02", "LOT02", "ITEM03", 30m, "자재창고", "가공동") })
        Exec(tx, "INSERT INTO IVT_MATERIAL_TX (TX_ID,LOT_ID,MATERIAL_ID,TX_TYPE,QTY,FROM_WAREHOUSE,TO_WAREHOUSE,TX_AT,PROCESSED_BY,STATUS) VALUES (@id,@lot,@mat,'Move',@qty,@from,@to,@at,'admin','Completed')",
            ("@id", m.Item1), ("@lot", m.Item2), ("@mat", m.Item3), ("@qty", m.Item4), ("@from", m.Item5), ("@to", m.Item6), ("@at", now));

    // MDM_VENDOR/ITEM(벤더 관리 화면용, V059) — FK: 품목 선삽입됨(ITEM03).
    foreach (var v in new[] { ("VEN_A", "대한자재", "Material"), ("VEN_B", "한빛부품", "Part") })
        Exec(tx, "INSERT INTO MDM_VENDOR (VENDOR_ID,VENDOR_NAME,VENDOR_TYPE,CORPORATION_NO,OWNER_NAME,PHONE,EMAIL,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@type,'123-45-67890','대표','02-000-0000','vendor@x.com',1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", v.Item1), ("@name", v.Item2), ("@type", v.Item3), ("@at", now));
    foreach (var vi in new[] { ("VI01", "VEN_A", "ITEM03", 7m, 100m, 1500m), ("VI02", "VEN_B", "ITEM02", 14m, 50m, 3200m) })
        Exec(tx, "INSERT INTO MDM_VENDOR_ITEM (VENDOR_ITEM_ID,VENDOR_ID,PRODUCT_ID,LEAD_TIME_DAYS,MOQ,BASE_PRICE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@ven,@prod,@lt,@moq,@price,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", vi.Item1), ("@ven", vi.Item2), ("@prod", vi.Item3), ("@lt", vi.Item4), ("@moq", vi.Item5), ("@price", vi.Item6), ("@at", now));

    // POM_WORK_ORDER(W/O 관리/현황 화면용, V060) — POM_LOT.WORK_ORDER_ID가 가리키는 본체(기존 보류 해소).
    foreach (var wo in new[] { ("WO01", "PLANT01", "완제품 A 1차 작업", "EQ01", 500m, 300m, "Started"), ("WO02", "PLANT01", "완제품 A 2차 작업", "EQ02", 300m, 0m, "Created") })
        Exec(tx, "INSERT INTO POM_WORK_ORDER (WORK_ORDER_ID,PLANT_ID,WORK_ORDER_NAME,EQUIPMENT_ID,WORK_ORDER_TYPE,PRODUCT_ID,PLAN_START_DATE,PLAN_QTY,START_QTY,STATUS,IS_HOLD,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@plant,@name,@eq,'Normal','ITEM01',@at,@plan,@start,@st,'N','SYSTEM',@at,'SYSTEM',@at)",
            ("@id", wo.Item1), ("@plant", wo.Item2), ("@name", wo.Item3), ("@eq", wo.Item4), ("@plan", wo.Item5), ("@start", wo.Item6), ("@st", wo.Item7), ("@at", now));

    // COM_ACTION/ALARM_ACTION(알람 액션 화면용, V061) — FK: 액션 선삽입.
    foreach (var ac in new[] { ("ACT_MAIL", "알람 메일 발송", "Email"), ("ACT_HOLD", "LOT 홀드", "Hold") })
        Exec(tx, "INSERT INTO COM_ACTION (ACTION_ID,ACTION_NAME,ACTION_TYPE,EMAIL_TITLE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@name,@type,'설비 알람 발생',1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", ac.Item1), ("@name", ac.Item2), ("@type", ac.Item3), ("@at", now));
    foreach (var aa in new[] { ("AA01", "ALM01", "ACT_MAIL", 1), ("AA02", "ALM01", "ACT_HOLD", 2) })
        Exec(tx, "INSERT INTO COM_ALARM_ACTION (ALARM_ACTION_ID,ALARM_ID,ACTION_ID,ACTION_SEQUENCE,IS_ACTIVE,CREATED_BY,CREATED_AT,UPDATED_BY,UPDATED_AT) " +
                 "VALUES (@id,@alarm,@act,@seq,1,'SYSTEM',@at,'SYSTEM',@at)",
            ("@id", aa.Item1), ("@alarm", aa.Item2), ("@act", aa.Item3), ("@seq", aa.Item4), ("@at", now));

    // SYS_REQUEST_LOG(요청 로그 뷰어 화면용, V062) — 실기록은 RequestLogMiddleware(기본 OFF)가 담당, 데모 2행.
    foreach (var rl in new[] { ("RL01", "POST", "/api/v1/query/MDM.PlantList", 200, 12), ("RL02", "POST", "/api/v1/auth/login", 401, 35) })
        Exec(tx, "INSERT INTO SYS_REQUEST_LOG (LOG_ID,METHOD,PATH,STATUS_CODE,ELAPSED_MS,USER_ID,CLIENT_IP,REQUESTED_AT) " +
                 "VALUES (@id,@m,@p,@st,@ms,'admin','127.0.0.1',@at)",
            ("@id", rl.Item1), ("@m", rl.Item2), ("@p", rl.Item3), ("@st", rl.Item4), ("@ms", rl.Item5), ("@at", now));

    // SYS_APP_LOG(로그 뷰어 화면용, V064) — 실기록은 DbLoggerProvider(기본 OFF)가 담당, 데모 2행.
    foreach (var al in new[] {
        ("AL01", "Warning", "NexaOne.FDC.Application", "수집 파라미터 임계 접근: EQ01/TEMP 78.5"),
        ("AL02", "Error", "NexaOne.Server.Gateway", "명명 쿼리 실행 실패: timeout (재시도 성공)") })
        Exec(tx, "INSERT INTO SYS_APP_LOG (LOG_ID,LOG_LEVEL,CATEGORY,MESSAGE,LOGGED_AT) VALUES (@id,@lvl,@cat,@msg,@at)",
            ("@id", al.Item1), ("@lvl", al.Item2), ("@cat", al.Item3), ("@msg", al.Item4), ("@at", now));

    tx.Commit();
    Console.WriteLine("[NexaOne.Server] MDM/QMS master data seeded (core + V035 ext: class/segment/process/routing/bom/qtime).");
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
