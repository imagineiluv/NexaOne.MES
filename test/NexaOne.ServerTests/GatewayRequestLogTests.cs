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

/// <summary>요청 로그 슬라이스 E2E — modules OFF + SQLite + RequestLogging:Enabled=true. RequestLogMiddleware가
/// /api/* 요청을 SYS_REQUEST_LOG(V062)에 실제 기록하고, SYSTEM2 REQLOG 화면 백엔드(SYS.RequestLogList)가 그 행을
/// 반환하는지 미들웨어→테이블→명명쿼리 전 구간을 검증한다(기본 OFF 게이트는 다른 픽스처들이 미기록으로 입증).</summary>
public sealed class GatewayRequestLogTests : IClassFixture<GatewayRequestLogTests.ReqLogFactory>
{
    private const string Secret = "reqlog-gateway-e2e-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-reqlog-test";
    private readonly ReqLogFactory _factory;
    public GatewayRequestLogTests(ReqLogFactory factory) => _factory = factory;

    public sealed class ReqLogFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-reqlog-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("RequestLogging:Enabled", "true");   // 미들웨어 게이트 ON — 실기록 검증
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    private HttpClient AuthedClient(string userId)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer,
            new[] { new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(NexaOne.Common.Security.Permissions.ClaimType, "sys:manage") },
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Api_request_is_logged_and_visible_via_named_query()
    {
        var user = $"reqlog_{Guid.NewGuid():N}"[..20];
        var client = AuthedClient(user);

        // 임의 API 호출 1건 — 미들웨어가 SYS_REQUEST_LOG에 기록해야 한다(200이든 아니든 기록 대상).
        (await client.PostAsJsonAsync("/api/v1/query/SYS.ListRoles", new Dictionary<string, object>()))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // REQLOG 화면 백엔드로 조회 — 방금 요청의 행이 사용자/경로/상태와 함께 반환돼야 한다.
        var res = await client.PostAsJsonAsync("/api/v1/query/SYS.RequestLogList",
            new Dictionary<string, object> { ["userId"] = user });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();

        var logged = rows!.Where(r => r["PATH"].ToString() == "/api/v1/query/SYS.ListRoles").ToList();
        logged.Should().NotBeEmpty("미들웨어가 /api 요청을 SYS_REQUEST_LOG에 기록해야 한다(REQLOG 점등 원천)");
        logged[0]["METHOD"].ToString().Should().Be("POST");
        int.Parse(logged[0]["STATUS_CODE"].ToString()!).Should().Be(200);
        logged[0]["USER_ID"].ToString().Should().Be(user, "인증 이후 배선이라 토큰 주체가 기록돼야 한다");
    }

    [Fact]
    public async Task Non_api_paths_are_not_logged()
    {
        var user = $"reqlog_{Guid.NewGuid():N}"[..20];
        var client = AuthedClient(user);

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.PostAsJsonAsync("/api/v1/query/SYS.RequestLogList", new Dictionary<string, object>());
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows!.Should().NotContain(r => r["PATH"].ToString() == "/health", "/api 외 경로(/health)는 기록 대상이 아니다");
    }
}
