using Microsoft.AspNetCore.Components.Authorization;
using NexaOne.Web;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// §17.4 — OpenTelemetry 분산 추적. AspNetCore(서버 요청) + HttpClient(→ API 호출) 계측으로
// Web↔API 호출을 한 트레이스로 상관시킨다. OTLP 엔드포인트 설정 시 Collector로, 없으면 개발용 Console.
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "NexaOne.Web", serviceVersion: "2.0"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else
            tracing.AddConsoleExporter();
    });

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());
builder.Services.AddScoped<AuthTokenService>();
builder.Services.AddScoped<AuthContextService>();
builder.Services.AddScoped<IAuthContext>(sp => sp.GetRequiredService<AuthContextService>());
builder.Services.AddScoped<NexaHubService>();
// 상태 배너(HubStatusBar)가 구상 NexaHubService 대신 계약에만 의존하도록 — 동일 인스턴스 포워딩(테스트 대체 가능).
builder.Services.AddScoped<IHubStatusSource>(sp => sp.GetRequiredService<NexaHubService>());
builder.Services.AddScoped<WaitOverlayService>();
// API 실패(403 권한 거부/5xx)를 전역 토스트로 노출하는 통지 채널 — ApiClient 발신 / ApiToastHost 구독
builder.Services.AddScoped<ApiNotificationService>();
builder.Services.AddScoped<DirtyTracker>();
builder.Services.AddScoped<MenuCacheService>();
builder.Services.AddScoped<MdiTabService>();
// Phase 3 — 메타데이터 화면 런타임: UiId→ScreenDefinition 해석기(싱글톤 시드). /meta/{uiId}가 동적 렌더.
builder.Services.AddSingleton<NexaOne.Web.Services.Meta.IScreenDefinitionProvider,
    NexaOne.Web.Services.Meta.InMemoryScreenDefinitionProvider>();
// §20.12: 즐겨찾기/최근 메뉴 개인화 — NavMenu와 MdiTabBar(최근 기록)가 서킷 내에서 공유
builder.Services.AddScoped<MenuPersonalizationService>();
// §20.9: 탭 전환(=페이지 dispose) 후 복귀 시 FDC 감시 상태를 복원하기 위한 서킷 수명 상태
builder.Services.AddScoped<NexaOne.Web.Services.Realtime.FdcMonitorState>();

// 개발 전용: 정적 SSR 단계의 [Authorize] 엔드포인트 인가를 통과시키는 자동 인증 스킴.
// 실제 화면 인증은 클라이언트 JWT(JwtAuthStateProvider)가 담당한다 — 이 스킴은 인증 서비스 부재로 SSR이
// 500나는 것을 막는 로컬 실행용이며 보안 경계가 아니다. 운영(비-Development)에선 등록하지 않는다.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication("DevAuto")
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
            NexaOne.Web.Services.Auth.DevAutoAuthHandler>("DevAuto", null);
    builder.Services.AddAuthorization();
}

var apiBase = builder.Configuration["ApiBaseUrl"]
    ?? throw new InvalidOperationException("ApiBaseUrl is required");

builder.Services.AddTransient<DefaultRequestTimeoutHandler>();
builder.Services.AddHttpClient<IApiClient, ApiClient>(c =>
{
    c.BaseAddress = new Uri(apiBase);
    // §20.11: 전역 Timeout은 배포 패키지 업로드(최대 500MB)의 상한 — 일반 요청은
    // DefaultRequestTimeoutHandler가 기본 100초로 제한한다 (Timeout은 요청별 연장이 불가능).
    c.Timeout = TimeSpan.FromMinutes(10);
}).AddHttpMessageHandler<DefaultRequestTimeoutHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
// 개발 전용 자동 인증/인가(위 DevAuto 스킴) — SSR [Authorize] 엔드포인트가 인증 서비스 부재로 500나지 않게 한다.
if (app.Environment.IsDevelopment())
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseAntiforgery();

// Phase 2 셸 임베드 — React SPA(wwwroot/spa)를 /spa로 서빙한다. 정적 자산은 UseStaticFiles가 처리하고,
// /spa 하위의 비파일 경로(클라이언트 라우팅)는 SPA index.html로 폴백한다(Blazor 라우팅은 그대로 유지).
app.MapFallbackToFile("/spa/{*path:nonfile}", "/spa/index.html");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
