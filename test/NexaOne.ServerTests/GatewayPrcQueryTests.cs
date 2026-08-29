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

/// <summary>게이트웨이 우선 PRC read E2E — modules OFF + SQLite. 레거시 PRC_TB_PURCHASE_ORDER를 V052로 포팅한
/// PRC_PURCHASE_ORDER를 SqliteConnection 직접 INSERT로 시드한 뒤 명명 read 쿼리(PRC.PurchaseOrderList) 라운드트립을
/// 검증한다(구매오더 관리/현황 점등 백엔드). + 미인증 401.</summary>
public sealed class GatewayPrcQueryTests : IClassFixture<GatewayPrcQueryTests.PrcFactory>
{
    private const string Secret = "prc-gateway-e2e-jwt-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-prc-test";
    private readonly PrcFactory _factory;
    public GatewayPrcQueryTests(PrcFactory factory) => _factory = factory;

    public sealed class PrcFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-prc-e2e-{Guid.NewGuid():N}.db");
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
            new[] { new Claim(ClaimTypes.NameIdentifier, "prc-e2e-user"),
                new Claim(NexaOne.Common.Security.Permissions.ClaimType, "prc:read") },
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SeedOrder(string orderId, string plantId, string vendorId, string status)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO PRC_PURCHASE_ORDER
            (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, VENDOR_ID, ORDER_DATE, ORDER_QTY, OWNER_ID, STATUS, IS_HOLD)
            VALUES (@id, @plant, '자재 발주', @vendor, '2026-05-01 00:00:00', 100, 'admin', @st, 'N')";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@plant", plantId);
        cmd.Parameters.AddWithValue("@vendor", vendorId);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/PRC.PurchaseOrderList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task PurchaseOrderList_returns_seeded_and_status_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var ordered = $"PO_{Suffix()}";
        var draft = $"PO_{Suffix()}";
        SeedOrder(ordered, plant, "VEN01", "Ordered");
        SeedOrder(draft, plant, "VEN02", "Draft");

        var all = await Query("PRC.PurchaseOrderList", new() { ["plantId"] = plant });
        all.Select(r => r["PURCHASE_ORDER_ID"].ToString()).Should().Contain(new[] { ordered, draft },
            "공장 발주가 조회돼야 한다(구매오더 관리/현황 점등)");
        all.Should().OnlyContain(r => r.ContainsKey("ORDER_QTY") && r.ContainsKey("STATUS"));

        var orderedOnly = await Query("PRC.PurchaseOrderList", new() { ["plantId"] = plant, ["status"] = "Ordered" });
        var ids = orderedOnly.Select(r => r["PURCHASE_ORDER_ID"].ToString()).ToList();
        ids.Should().Contain(ordered);
        ids.Should().NotContain(draft, "status 필터는 해당 상태만 반환");
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
