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

    private void SeedOrder(string orderId, string plantId, string customerId, string status)
        => Exec(@"INSERT INTO SLS_SALES_ORDER
            (SALES_ORDER_ID, PLANT_ID, SALES_ORDER_NAME, CUSTOMER_ID, PRODUCT_ID, PLAN_START_DATE, PLAN_QTY, DELIVERED_QTY, OWNER_ID, STATUS, IS_HOLD)
            VALUES (@id, @plant, '판매 오더', @cust, 'ITEM01', '2026-06-01 00:00:00', 500, 0, 'admin', @st, 'N')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@cust", customerId);
            cmd.Parameters.AddWithValue("@st", status);
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

    private void SeedReferences(string plantId, string customerId, string productId, bool customerActive = true)
    {
        Exec("INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@id, @name)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", plantId);
            cmd.Parameters.AddWithValue("@name", $"공장 {plantId}");
        });
        Exec(@"INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
               VALUES (@id, @name, @active)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", customerId);
            cmd.Parameters.AddWithValue("@name", $"고객 {customerId}");
            cmd.Parameters.AddWithValue("@active", customerActive ? 1 : 0);
        });
        Exec(@"INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
               VALUES (@id, @name, 'FinishedGoods', 'EA', 'Valid')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", productId);
            cmd.Parameters.AddWithValue("@name", $"품목 {productId}");
        });
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
    }

    [Fact]
    public async Task CreateSalesOrder_persists_dates_draft_status_and_jwt_audit()
    {
        EnsureSchemaReady();
        var suffix = Suffix();
        var plant = $"PLANT_{suffix}";
        var customer = $"CUSTOMER_{suffix}";
        var product = $"PRODUCT_{suffix}";
        var order = $"SO_{suffix}";
        var actor = $"sales-manager-{suffix}";
        SeedReferences(plant, customer, product);

        var response = await AuthedClient(actor, "sls:manage").PostAsJsonAsync(
            "/api/v1/command/SLS.CreateSalesOrder",
            new Dictionary<string, object?>
            {
                ["salesOrderId"] = order,
                ["plantId"] = plant,
                ["salesOrderName"] = "7월 판매 계획",
                ["customerId"] = customer,
                ["productId"] = product,
                ["planStartDate"] = "2026-07-15",
                ["planEndDate"] = "2026-07-31",
                ["planQty"] = 125.5m,
                // 클라이언트가 상태/감사 사용자를 보낼 수 없고 SQL이 JWT 값을 사용한다.
                ["status"] = "Closed",
                ["currentUser"] = "spoofed-user",
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected.Should().Be(1);

        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT SALES_ORDER_NAME, PLAN_START_DATE, PLAN_END_DATE, PLAN_QTY,
                                   STATUS, OWNER_ID, CREATED_BY, UPDATED_BY
                            FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID = @id";
        cmd.Parameters.AddWithValue("@id", order);
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("7월 판매 계획");
        reader.GetValue(1).ToString().Should().StartWith("2026-07-15");
        reader.GetValue(2).ToString().Should().StartWith("2026-07-31");
        Convert.ToDecimal(reader.GetValue(3)).Should().Be(125.5m);
        reader.GetString(4).Should().Be("Draft", "신규 주문 상태는 서버가 Draft로 고정한다");
        reader.GetString(5).Should().Be(actor);
        reader.GetString(6).Should().Be(actor);
        reader.GetString(7).Should().Be(actor);
    }

    [Fact]
    public async Task CreateSalesOrder_rejects_bad_quantity_due_date_and_inactive_reference()
    {
        EnsureSchemaReady();
        var suffix = Suffix();
        var plant = $"PLANT_{suffix}";
        var customer = $"CUSTOMER_{suffix}";
        var product = $"PRODUCT_{suffix}";
        SeedReferences(plant, customer, product, customerActive: false);

        async Task<int> Save(string orderId, decimal qty, string start, string? due, string customerId)
        {
            var response = await AuthedClient("sales-validator", "sls:manage").PostAsJsonAsync(
                "/api/v1/command/SLS.CreateSalesOrder",
                new Dictionary<string, object?>
                {
                    ["salesOrderId"] = orderId, ["plantId"] = plant, ["salesOrderName"] = "검증 주문",
                    ["customerId"] = customerId, ["productId"] = product, ["planQty"] = qty,
                    ["planStartDate"] = start, ["planEndDate"] = due,
                });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected;
        }

        (await Save($"SO_QTY_{suffix}", 0, "2026-07-20", "2026-07-31", customer)).Should().Be(0);
        (await Save($"SO_DATE_{suffix}", 1, "2026-08-01", "2026-07-31", customer)).Should().Be(0);
        (await Save($"SO_DUE_{suffix}", 1, "2026-07-20", null, customer)).Should().Be(0);
        (await Save($"SO_CUST_{suffix}", 1, "2026-07-20", "2026-07-31", customer)).Should().Be(0);
    }

    [Fact]
    public async Task Update_and_delete_are_limited_to_draft_orders()
    {
        EnsureSchemaReady();
        var suffix = Suffix();
        var plant = $"PLANT_{suffix}";
        var customer = $"CUSTOMER_{suffix}";
        var product = $"PRODUCT_{suffix}";
        var order = $"SO_{suffix}";
        SeedReferences(plant, customer, product);
        var client = AuthedClient("sales-guard", "sls:manage");
        var initial = new Dictionary<string, object?>
        {
            ["salesOrderId"] = order, ["plantId"] = plant, ["salesOrderName"] = "초안 이름",
            ["customerId"] = customer, ["productId"] = product, ["planQty"] = 10,
            ["planStartDate"] = "2026-07-15", ["planEndDate"] = "2026-07-31",
        };
        var created = await client.PostAsJsonAsync("/api/v1/command/SLS.CreateSalesOrder", initial);
        (await created.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected.Should().Be(1);

        // Draft 편집은 허용한다.
        initial["salesOrderName"] = "초안 수정";
        var draftUpdate = await client.PostAsJsonAsync("/api/v1/command/SLS.CreateSalesOrder", initial);
        (await draftUpdate.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected.Should().Be(1);

        // 확정 뒤에는 같은 upsert와 삭제가 모두 0행이어야 한다.
        var confirmed = await client.PostAsJsonAsync("/api/v1/command/SLS.ConfirmSalesOrder",
            new Dictionary<string, object?> { ["salesOrderId"] = order });
        (await confirmed.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected.Should().Be(1);
        initial["salesOrderName"] = "확정 뒤 변조";
        var blockedUpdate = await client.PostAsJsonAsync("/api/v1/command/SLS.CreateSalesOrder", initial);
        (await blockedUpdate.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected.Should().Be(0);
        var blockedDelete = await client.PostAsJsonAsync("/api/v1/command/SLS.DeleteSalesOrder",
            new Dictionary<string, object?> { ["salesOrderId"] = order });
        (await blockedDelete.Content.ReadFromJsonAsync<AffectedResponse>())!.Affected.Should().Be(0);

        var rows = await Query("SLS.SalesOrderList", new() { ["plantId"] = plant });
        var persisted = rows.Single(row => row["SALES_ORDER_ID"].ToString() == order);
        persisted["SALES_ORDER_NAME"].ToString().Should().Be("초안 수정");
        persisted["STATUS"].ToString().Should().Be("Confirmed");
    }

    private sealed record AffectedResponse(int Affected);

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var res = await AuthedClient().PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
