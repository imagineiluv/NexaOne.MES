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

/// <summary>React SPA 정적 서빙(Phase 4) — 호스트(NexaOne.Server)가 wwwroot/spa를 정적 서빙하고,
/// 파일이 아닌 /spa/* 경로를 index.html 셸로 폴백(MapFallbackToFile)하는지 검증한다.
/// npm 빌드 의존 없이 결정론적으로 동작하도록, 호스트 web root에 더미 index.html을 직접 깔고 테스트한다.
/// 핵심: 명시적 api/v1 라우트가 SPA 폴백에 가려지지 않음(라우팅 우선순위)을 함께 입증한다.</summary>
public sealed class SpaStaticServingTests : IClassFixture<SpaStaticServingTests.SpaFactory>
{
    private const string Secret = "phase4-spa-static-serving-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-test";
    private const string DummyIndexHtml = "<!doctype html><html><body><div id=\"root\">SPA-OK</div></body></html>";
    private readonly SpaFactory _factory;
    public SpaStaticServingTests(SpaFactory factory) => _factory = factory;

    public sealed class SpaFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-spa-static-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");   // 게이트웨이는 plugin 무관 — 순수 웹 셸
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);

            // 결정론적 web root — content root(=NexaOne.Server 프로젝트 디렉터리)를 명시하고, 그 아래 wwwroot/spa/
            // index.html 더미를 호스트 빌드 '전에' 깐다. 호스트 빌드 시점에 폴더가 실존해야 ASP.NET이 WebRootPath와
            // WebRootFileProvider를 그 디렉터리로 고정하므로(폴더 부재면 null이 되어 정적 서빙/폴백이 깨진다),
            // CreateHost가 아니라 여기(ConfigureWebHost)에서 선제 생성하는 것이 핵심이다. npm 빌드 의존 제거 + 병렬 무관.
            var serverDir = RepositorySource.GetDirectory(
                "src", "00.Main", "NexaOne.Server");
            var webRoot = Path.Combine(serverDir, "wwwroot");
            var indexPath = Path.Combine(webRoot, "spa", "index.html");
            EnsureDummyIndex(indexPath);
            builder.UseContentRoot(serverDir);
            builder.UseWebRoot(webRoot);
        }

        // 우리가 만든 더미만 쓰고, 이미 npm 빌드 산출물이 있으면 보존한다. 동시 실행(병렬 테스트 클래스) 안전하게
        // 디렉터리 선생성 후 존재 확인(이미 있으면 no-op) — 더미는 gitignored라 Dispose에서 삭제하지 않는다.
        private static void EnsureDummyIndex(string indexPath)
        {
            if (File.Exists(indexPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            try { File.WriteAllText(indexPath, DummyIndexHtml); }
            catch (IOException) when (File.Exists(indexPath)) { /* 병렬 클래스가 먼저 생성 — 정상 */ }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            // 더미 index.html은 gitignored(**/wwwroot/spa/)이며 npm 빌드가 덮어쓰므로 의도적으로 남겨둔다
            // (병렬 클래스/후속 테스트가 같은 파일을 읽을 수 있어 삭제 시 레이스 위험).
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 DB 정리 실패 무시 */ }
        }
    }

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "spa-test-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Spa_fallback_serves_index_for_nonfile_path()
    {
        // /spa/dashboard는 실제 파일이 아닌 React BrowserRouter 클라이언트 라우트 → index.html 셸로 폴백돼야 한다.
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/spa/dashboard");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "파일이 아닌 /spa/* 경로는 MapFallbackToFile로 index.html을 반환해야 한다");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("id=\"root\"", "SPA 셸(index.html, React 마운트 지점)이 반환돼야 한다 — 더미·실 npm 빌드 공통 마커(빌드 상태 비의존)");
    }

    [Theory]
    [InlineData("/Designer")]
    [InlineData("/Designer/")]
    [InlineData("/Designer/DEMO_DESIGNER")]
    public async Task Designer_alias_serves_same_portal_shell(string path)
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "Designer 공개 경로와 하위 화면 경로는 /spa/index.html 셸로 폴백돼야 한다");
        (await res.Content.ReadAsStringAsync()).Should().Contain("id=\"root\"");
    }

    [Fact]
    public async Task Spa_fallback_does_not_shadow_health_endpoint()
    {
        // /health는 SPA 폴백과 무관하게 익명 200을 유지해야 한다(폴백은 /spa/* 만 매칭).
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "/health는 SPA 폴백에 가려지지 않아야 한다");
    }

    [Fact]
    public async Task Existing_api_route_not_shadowed_by_spa_fallback()
    {
        // api/v1 명시 라우트가 SPA 폴백보다 우선함을 입증 — GatewayMdmE2E와 동일한 command→query 라운드트립.
        var client = AuthedClient("mdm:manage");

        var plantId = "SPA_PLANT_" + Guid.NewGuid().ToString("N")[..8];
        var save = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        {
            ["plantId"] = plantId,
            ["plantName"] = "Phase4 SPA 공장",
            ["description"] = "phase4 spa fallback guard",
            ["country"] = "KR",
            ["timeZone"] = "Asia/Seoul",
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "api/v1 쓰기쿼리는 SPA 폴백에 가려지지 않고 200이어야 한다");

        var list = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new Dictionary<string, object> { ["plantId"] = plantId });
        list.StatusCode.Should().Be(HttpStatusCode.OK, "api/v1 조회쿼리도 SPA 폴백에 가려지지 않아야 한다");
        var rows = await list.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().ContainSingle(r => r.ContainsKey("PLANT_ID") && r["PLANT_ID"].ToString() == plantId,
            "저장한 공장이 명명 조회쿼리로 라운드트립돼야 한다(api/v1 라우트 우선)");
    }
}
