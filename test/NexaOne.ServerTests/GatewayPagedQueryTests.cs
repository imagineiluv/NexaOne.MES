using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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
        var client = AuthedClient("paged-reader", "mdm:read");

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
    public async Task Material_history_keeps_the_501st_row_reachable_through_the_next_page()
    {
        var client = AuthedClient("paged-ivt-reader", "ivt:read");
        await using (var connection = new SqliteConnection(_factory.ConnString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            for (var i = 0; i < 501; i++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO IVT_MATERIAL_TX
                        (TX_ID, TX_TYPE, TX_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES
                        (@id, 'PagedProof', '2030-01-01T00:00:00Z',
                         'paged-test', '2030-01-01T00:00:00Z',
                         'paged-test', '2030-01-01T00:00:00Z');
                    """;
                command.Parameters.AddWithValue("@id", $"PAGED_TX_{i:D4}");
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        var firstResponse = await client.PostAsJsonAsync("/api/v1/query/IVT.MaterialTxList/paged",
            new { parameters = new { txType = "PagedProof" }, limit = 500, offset = 0 });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await firstResponse.Content.ReadFromJsonAsync<PagedResponse>();
        first!.Total.Should().Be(501);
        first.Rows.Should().HaveCount(500);

        var tailResponse = await client.PostAsJsonAsync("/api/v1/query/IVT.MaterialTxList/paged",
            new { parameters = new { txType = "PagedProof" }, limit = 500, offset = 500 });
        tailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tail = await tailResponse.Content.ReadFromJsonAsync<PagedResponse>();
        tail!.Total.Should().Be(501);
        tail.Rows.Should().ContainSingle();
        tail.Rows[0]["TX_ID"]!.ToString().Should().Be("PAGED_TX_0000",
            "timestamp ties must be resolved by the stable TX_ID descending order");
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
        var sysClient = AuthedClient("paged-sys-admin", "sys:manage");
        (await sysClient.PostAsJsonAsync("/api/v1/query/SYS.AppLogList/paged",
            new { parameters = new { }, limit = 10, offset = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
