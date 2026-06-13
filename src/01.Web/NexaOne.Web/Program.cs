using Microsoft.AspNetCore.Components.Authorization;
using NexaOne.Web;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());
builder.Services.AddScoped<AuthTokenService>();
builder.Services.AddScoped<AuthContextService>();
builder.Services.AddScoped<NexaHubService>();
builder.Services.AddScoped<WaitOverlayService>();
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
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
