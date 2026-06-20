using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>통합 호스트 Blazor 서빙(Phase 4 Task 4) — 호스트(NexaOne.Server)가 MapRazorComponents&lt;HostApp&gt;로
/// Blazor 호스트 문서를 서빙해 /login·/meta 라우트가 Blazor 페이지로 도달 가능함을 검증한다.
/// prerender:false(설계 §4)라 GET은 서버렌더 컴포넌트 본문이 아니라 HostApp 셸(_framework/blazor.web.js 포함)을
/// 반환한다 — 따라서 로그인 폼 필드가 아니라 '셸'(프레임워크 스크립트)에 단언한다. RCL의 MetaScreen/렌더러 자체는
/// NexaOne.UnitTests의 bUnit이 이미 커버하므로(중복 금지) 여기선 '호스트가 Blazor 문서를 서빙하는가'만 입증한다.
/// 추가로 api/v1·/health가 Blazor/폴백에 가려지지 않음(라우팅 우선순위)을 함께 입증한다.</summary>
public sealed class HostBlazorServingTests : IClassFixture<HostBlazorServingTests.BlazorHostFactory>
{
    private const string Secret = "phase4-host-blazor-serving-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-test";
    // HostApp.razor가 방출하는 Blazor Web App 부트스트랩 스크립트 — 이 마커가 있으면 호스트가 Blazor 호스트 문서를 서빙한 것.
    private const string BlazorFrameworkScript = "_framework/blazor.web.js";
    private readonly BlazorHostFactory _factory;
    public HostBlazorServingTests(BlazorHostFactory factory) => _factory = factory;

    public sealed class BlazorHostFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-host-blazor-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");   // Blazor 서빙은 plugin 무관 — 순수 웹 셸
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 DB 정리 실패 무시 */ }
        }
    }

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "blazor-host-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Login_page_serves_blazor_host_document()
    {
        // /login은 호스트 로컬 Blazor 페이지(@page "/login") → MapRazorComponents<HostApp>가 호스트 문서를 서빙해야 한다.
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/login");

        res.StatusCode.Should().Be(HttpStatusCode.OK, "/login은 Blazor 호스트 문서로 200을 반환해야 한다(라우트 미연결이면 404 — 실제 배선 버그)");
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html", "Blazor 호스트 문서는 HTML이어야 한다");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain(BlazorFrameworkScript,
            "HostApp 셸이 Blazor 부트스트랩 스크립트를 포함해야 한다 — MapRazorComponents<HostApp> 서빙 입증(prerender:false라 폼 본문 대신 셸)");
    }

    [Fact]
    public async Task Meta_route_serves_blazor_host_document()
    {
        // /meta/{uiId}는 RCL(NexaOne.Web.Components)의 Blazor 페이지 → 호스트가 같은 Blazor 앱으로 서빙해야 한다.
        // 핵심: 라우트 가능 컴포넌트 엔드포인트 탐색은 루트(HostApp) 어셈블리만 스캔하므로, 호스트가
        // MapRazorComponents<HostApp>().AddAdditionalAssemblies(RCL)로 RCL을 명시 등록해야 /meta가 서버 엔드포인트로
        // 잡힌다(미등록이면 404 — Task 3 배선 공백). 또한 MetaScreen는 @attribute [Authorize]라 엔드포인트 인가가
        // 정적 SSR 요청 단계에서 평가된다: 익명은 401, 유효 Bearer는 호스트 문서(셸)를 200으로 받는다. prerender:false라
        // GET은 컴포넌트 본문이 아닌 HostApp 셸(_framework/blazor.web.js 포함)을 반환하므로 셸 마커에 단언한다.
        var client = AuthedClient(); // /meta는 [Authorize] → 엔드포인트 인가 통과를 위해 유효 Bearer 필요
        var res = await client.GetAsync("/meta/DEMO_GRID");

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "/meta/* 라우트가 호스트 Blazor 앱(AddAdditionalAssemblies로 RCL 등록)에 연결돼 인증 요청에 200 호스트 문서를 반환해야 한다");
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html", "Blazor 호스트 문서는 HTML이어야 한다");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain(BlazorFrameworkScript,
            "/meta 라우트도 동일한 Blazor 호스트 문서(셸)를 서빙해야 한다 — RCL /meta가 호스트 Blazor 앱으로 도달 가능함을 입증");
    }

    [Fact]
    public async Task Meta_route_is_registered_endpoint_not_spa_fallback()
    {
        // [Authorize] /meta 엔드포인트가 실제로 등록됐음을 음수 단언으로 보강 — 미인증 요청은 401(엔드포인트 인가 챌린지)이어야
        // 한다. 만약 라우트가 미등록이면 404(엔드포인트 부재)가 나므로, 401은 'RCL /meta 엔드포인트가 호스트에 잡혔다'는 증거다.
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/meta/DEMO_GRID");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] /meta 엔드포인트가 등록돼 미인증 요청을 401로 챌린지해야 한다(404면 엔드포인트 미등록 = 배선 공백)");
    }

    [Fact]
    public async Task Api_and_health_unaffected_by_blazor()
    {
        // Blazor/폴백 매핑이 명시 라우트를 가리지 않음을 입증 — /health 익명 200 + api/v1 command→query 라운드트립.
        var anon = _factory.CreateClient();
        var health = await anon.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK, "/health는 Blazor 라우팅과 무관하게 익명 200을 유지해야 한다");

        var client = AuthedClient("mdm:manage");
        var plantId = "BLZ_PLANT_" + Guid.NewGuid().ToString("N")[..8];
        var save = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        {
            ["plantId"] = plantId,
            ["plantName"] = "Phase4 Blazor 공장",
            ["description"] = "phase4 blazor host guard",
            ["country"] = "KR",
            ["timeZone"] = "Asia/Seoul",
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "api/v1 쓰기쿼리는 Blazor/폴백에 가려지지 않고 200이어야 한다");

        var list = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new Dictionary<string, object> { ["plantId"] = plantId });
        list.StatusCode.Should().Be(HttpStatusCode.OK, "api/v1 조회쿼리도 Blazor 라우팅에 가려지지 않아야 한다");
        var rows = await list.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().ContainSingle(r => r.ContainsKey("PLANT_ID") && r["PLANT_ID"].ToString() == plantId,
            "저장한 공장이 명명 조회쿼리로 라운드트립돼야 한다(api/v1 라우트가 Blazor보다 우선)");
    }
}
