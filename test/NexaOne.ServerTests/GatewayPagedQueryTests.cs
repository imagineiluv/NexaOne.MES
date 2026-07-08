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

/// <summary>제네릭 서버 페이징 게이트웨이 E2E(/api/v1/query/{id}/paged) — modules OFF + SQLite.
/// (1)total+창 슬라이스 (2)offset 이동 (3)쓰기 쿼리 400 (4)자체 상한 쿼리 422(클라 폴백 신호)를 실검증한다.
/// SQL 조립 규칙 자체는 PagedSqlBuilderTests(순수)가 가드.</summary>
public sealed class GatewayPagedQueryTests : IClassFixture<GatewayPagedQueryTests.PagedFactory>
{
    private const string Secret = "paged-query-gateway-e2e-jwt-secret-32bytes!!";
    private const string Issuer = "nexaone-paged-test";
    private readonly PagedFactory _factory;
    public GatewayPagedQueryTests(PagedFactory factory) => _factory = factory;

    public sealed class PagedFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-paged-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
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

    private HttpClient AuthedClient(string userId, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed record PagedResponse(int Total, List<Dictionary<string, object?>> Rows);

    [Fact]
    public async Task Paged_returns_total_and_window_and_offset_moves()
    {
        var client = AuthedClient("paged-reader");

        // dev 시드 공장 2행 — limit=1: total=2 + 1행.
        var r1 = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantList/paged",
            new { parameters = new { }, limit = 1, offset = 0 });
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var p1 = await r1.Content.ReadFromJsonAsync<PagedResponse>();
        p1!.Total.Should().BeGreaterThanOrEqualTo(2, "dev 시드 공장이 2행 이상");
        p1.Rows.Should().HaveCount(1, "limit=1 창");

        // offset=1 — 다음 행(다른 PLANT_ID).
        var r2 = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantList/paged",
            new { parameters = new { }, limit = 1, offset = 1 });
        var p2 = await r2.Content.ReadFromJsonAsync<PagedResponse>();
        p2!.Rows.Should().HaveCount(1);
        p2.Rows[0]["PLANT_ID"]!.ToString().Should().NotBe(p1.Rows[0]["PLANT_ID"]!.ToString(), "offset 이동 시 다른 행");
        p2.Total.Should().Be(p1.Total, "total은 창과 무관하게 동일");
    }

    [Fact]
    public async Task Paged_rejects_write_query_and_own_limit_query()
    {
        var client = AuthedClient("paged-any", "mdm:manage");

        // 쓰기 쿼리 → 400 (WRITE_QUERY_VIA_QUERY와 동일 규칙).
        (await client.PostAsJsonAsync("/api/v1/query/MDM.CreateVendor/paged",
            new { parameters = new { }, limit = 10, offset = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 자체 상한 쿼리(@limit/@offset 수동 페이징 — SYS.AppLogList) → 422(클라 전량 폴백 신호).
        (await client.PostAsJsonAsync("/api/v1/query/SYS.AppLogList/paged",
            new { parameters = new { }, limit = 10, offset = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
