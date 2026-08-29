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

/// <summary>게이트웨이 우선 FACTORY_STD BOR(자원명세) read E2E — modules OFF + SQLite. V058(MDM_BOR/MDM_BOR_RESOURCE)를
/// 직접 시드한 뒤 명명 read 쿼리(MDM.BorList/BorResourceList) 라운드트립을 검증한다(BOR 조건/자원 기준 점등 백엔드). + 미인증 401.</summary>
public sealed class GatewayStdBorQueryTests : IClassFixture<GatewayStdBorQueryTests.BorFactory>
{
    private const string Secret = "std-bor-gateway-e2e-jwt-secret-key-32bytes!";
    private const string Issuer = "nexaone-bor-test";
    private readonly BorFactory _factory;
    public GatewayStdBorQueryTests(BorFactory factory) => _factory = factory;

    public sealed class BorFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-bor-e2e-{Guid.NewGuid():N}.db");
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

    private void EnsureSchemaReady() => _ = _factory.CreateClient();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer,
            new[] { new Claim(ClaimTypes.NameIdentifier, "bor-e2e-user"),
                new Claim(NexaOne.Common.Security.Permissions.ClaimType, "mdm:read") },
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private void SeedBor(string borId, string plantId, string borType)
        => Exec("INSERT INTO MDM_BOR (BOR_ID, PLANT_ID, BOR_NAME, PROCESS_ID, PRODUCT_ID, BOR_TYPE, IS_ACTIVE) VALUES (@id, @plant, 'BOR', 'PROC1', 'ITEM01', @type, 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", borId); cmd.Parameters.AddWithValue("@plant", plantId); cmd.Parameters.AddWithValue("@type", borType); });

    private void SeedResource(string resourceId, string borId, string resourceType)
        => Exec("INSERT INTO MDM_BOR_RESOURCE (RESOURCE_ID, BOR_ID, RESOURCE_TYPE, RESOURCE_REF_ID, RESOURCE_NAME, REQUIRED_QTY, IS_ACTIVE) VALUES (@id, @bor, @type, 'EQ01', '자원', 2, 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", resourceId); cmd.Parameters.AddWithValue("@bor", borId); cmd.Parameters.AddWithValue("@type", resourceType); });

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/query/MDM.BorList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task BorList_returns_seeded_and_type_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var cond = $"BOR_{Suffix()}";
        var res = $"BOR_{Suffix()}";
        SeedBor(cond, plant, "Condition");
        SeedBor(res, plant, "Resource");

        var all = await Query("MDM.BorList", new() { ["plantId"] = plant });
        all.Select(r => r["BOR_ID"].ToString()).Should().Contain(new[] { cond, res }, "공장 BOR이 조회돼야 한다(BOR 조건 기준 점등)");

        var condOnly = await Query("MDM.BorList", new() { ["plantId"] = plant, ["borType"] = "Condition" });
        var ids = condOnly.Select(r => r["BOR_ID"].ToString()).ToList();
        ids.Should().Contain(cond);
        ids.Should().NotContain(res, "borType 필터는 해당 유형만 반환");
    }

    [Fact]
    public async Task BorResourceList_bor_filter_narrows()
    {
        EnsureSchemaReady();
        var bor = $"BOR_{Suffix()}";
        var mine = $"RSC_{Suffix()}";
        SeedBor(bor, "P1", "Resource");
        SeedResource(mine, bor, "Equipment");
        SeedResource($"RSC_{Suffix()}", $"BOR_{Suffix()}", "Tool"); // 다른 BOR 자원 — 제외

        var rows = await Query("MDM.BorResourceList", new() { ["borId"] = bor });
        rows.Select(r => r["RESOURCE_ID"].ToString()).Should().Contain(mine);
        rows.Should().OnlyContain(r => r["BOR_ID"].ToString() == bor, "borId 필터는 해당 BOR 자원만(BOR 자원 기준 점등)");
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var res = await AuthedClient().PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
