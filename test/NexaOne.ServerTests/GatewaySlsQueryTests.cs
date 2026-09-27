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

/// <summary>게이트웨이 우선 SLS read E2E — modules OFF + SQLite. 레거시 SLS_TB_SALES_ORDER/SLS_TB_SALES_REQUEST를
/// V053으로 포팅한 SLS_SALES_ORDER/SLS_SALES_REQUEST를 직접 시드한 뒤 명명 read 쿼리(SLS.SalesOrderList/
/// SalesRequestList) 라운드트립을 검증한다(수주/판매 요청 점등 백엔드). + 미인증 401.</summary>
public sealed class GatewaySlsQueryTests : IClassFixture<GatewaySlsQueryTests.SlsFactory>
{
    private const string Secret = "sls-gateway-e2e-jwt-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-sls-test";
    private readonly SlsFactory _factory;
    public GatewaySlsQueryTests(SlsFactory factory) => _factory = factory;

    public sealed class SlsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-sls-e2e-{Guid.NewGuid():N}.db");
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
        => AuthedClient("sls-e2e-user", "sls:read");

    private HttpClient AuthedClient(string userId, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(permissions.Select(permission =>
            new Claim(NexaOne.Common.Security.Permissions.ClaimType, permission)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void SeedOrder(string orderId, string plantId, string customerId, string status, bool held = false)
        => Exec(@"INSERT INTO SLS_SALES_ORDER
            (SALES_ORDER_ID, PLANT_ID, SALES_ORDER_NAME, CUSTOMER_ID, PRODUCT_ID, PLAN_START_DATE, PLAN_QTY, DELIVERED_QTY, OWNER_ID, STATUS, IS_HOLD)
            VALUES (@id, @plant, '판매 오더', @cust, 'ITEM01', '2026-06-01 00:00:00', 500, 0, 'admin', @st, @hold)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@cust", customerId);
            cmd.Parameters.AddWithValue("@st", status);
            cmd.Parameters.AddWithValue("@hold", held ? "Y" : "N");
        });

    private void SeedRequest(string requestId, string salesOrderId, string status)
        => Exec(@"INSERT INTO SLS_SALES_REQUEST
            (SALES_REQUEST_ID, SALES_REQUEST_NAME, SALES_ORDER_ID, CUSTOMER_ID, PRODUCT_ID, REQUEST_DATE, REQUEST_QTY, STATUS)
            VALUES (@id, '판매 요청', @so, 'CUST01', 'ITEM01', '2026-06-02 00:00:00', 100, @st)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", requestId);
            cmd.Parameters.AddWithValue("@so", salesOrderId);
            cmd.Parameters.AddWithValue("@st", status);
        });

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/SLS.SalesOrderList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task SalesOrderList_returns_seeded_and_status_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var confirmed = $"SO_{Suffix()}";
        var draft = $"SO_{Suffix()}";
        SeedOrder(confirmed, plant, "CUST01", "Confirmed");
        SeedOrder(draft, plant, "CUST02", "Draft");

        var all = await Query("SLS.SalesOrderList", new() { ["plantId"] = plant });
        all.Select(r => r["SALES_ORDER_ID"].ToString()).Should().Contain(new[] { confirmed, draft },
            "공장 수주가 조회돼야 한다(수주 관리 점등)");
        all.Should().OnlyContain(r => r.ContainsKey("PLAN_QTY") && r.ContainsKey("STATUS"));

        var confirmedOnly = await Query("SLS.SalesOrderList", new() { ["plantId"] = plant, ["status"] = "Confirmed" });
        var ids = confirmedOnly.Select(r => r["SALES_ORDER_ID"].ToString()).ToList();
        ids.Should().Contain(confirmed);
        ids.Should().NotContain(draft, "status 필터는 해당 상태만 반환");
    }

    [Fact]
    public async Task SalesRequestList_returns_seeded_and_order_filter_narrows()
    {
        EnsureSchemaReady();
        var order = $"SO_{Suffix()}";
        var linked = $"SR_{Suffix()}";
        var other = $"SR_{Suffix()}";
        SeedRequest(linked, order, "Draft");
        SeedRequest(other, $"SO_{Suffix()}", "Draft");

        var byOrder = await Query("SLS.SalesRequestList", new() { ["salesOrderId"] = order });
        var ids = byOrder.Select(r => r["SALES_REQUEST_ID"].ToString()).ToList();
        ids.Should().Contain(linked);
        ids.Should().NotContain(other, "salesOrderId 필터는 해당 오더 요청만 반환(판매 요청 점등)");

        var byRequest = await Query("SLS.SalesRequestList", new() { ["salesRequestId"] = linked });
        byRequest.Select(r => r["SALES_REQUEST_ID"].ToString()).Should().ContainSingle()
            .Which.Should().Be(linked, "재시도 충돌 후 알려진 요청 ID로 현재 연결 상태를 확인할 수 있어야 한다");
    }

    [Fact]
    public async Task Former_sales_order_write_queries_are_not_registered()
    {
        var client = AuthedClient("manager", "sls:manage");
        foreach (var id in new[]
                 {
                     "SLS.CreateSalesOrder", "SLS.DeleteSalesOrder",
                     "SLS.ConfirmSalesOrder", "SLS.CloseSalesOrder",
                 })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/command/{id}", new Dictionary<string, object?>());
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "수주 쓰기는 SLS 모듈 인터페이스만 사용해야 한다");
        }
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
