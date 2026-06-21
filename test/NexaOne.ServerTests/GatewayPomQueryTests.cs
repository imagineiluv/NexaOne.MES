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

/// <summary>게이트웨이 우선 POM read E2E(S6) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// POM_PRODUCTION_PLAN / POM_PRODUCTION_ORDER / POM_LOT 를 SqliteConnection 직접 INSERT로 시드한 뒤
/// 명명 read 쿼리(PlansByPlant / PlanCountByStatus / OrdersByPlan / OrdersByEquipment / LotsByPlant / LotsByWorkOrder)
/// 라운드트립을 검증한다. + 미인증 401. 실재 컬럼/NOT NULL(감사·ROUTE_STEPS 명시)·STATUS/LOT_STATE enum.ToString() 패리티를 충족한다.</summary>
public sealed class GatewayPomQueryTests : IClassFixture<GatewayPomQueryTests.PomFactory>
{
    private const string Secret = "s6-pom-gateway-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-pom-test";
    private readonly PomFactory _factory;
    public GatewayPomQueryTests(PomFactory factory) => _factory = factory;

    public sealed class PomFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-pom-e2e-{Guid.NewGuid():N}.db");
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

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "pom-e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    // POM_PRODUCTION_PLAN 1건 시드(실재 컬럼·NOT NULL 충족; 감사 컬럼 명시). STATUS는 enum.ToString() 패리티.
    private void SeedPlan(string planId, string plantId, string status, DateTime start, DateTime end)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_PRODUCTION_PLAN
            (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY, PLANNED_START_DATE, PLANNED_END_DATE, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, '주간계획', @plant, 'PROD01', 100, @start, @end, @st, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", planId);
        cmd.Parameters.AddWithValue("@plant", plantId);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    // POM_PRODUCTION_ORDER 1건 시드. FK(PLAN_ID→PLAN, EQUIPMENT_ID→MDM_EQUIPMENT)는 Foreign Keys=False라 무시된다.
    private void SeedOrder(string orderId, string planId, string equipmentId, string status)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_PRODUCTION_ORDER
            (ORDER_ID, PLAN_ID, EQUIPMENT_ID, PRODUCT_ID, ORDER_QTY, SCHEDULED_START, SCHEDULED_END, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @plan, @eq, 'PROD01', 50, @now, @now, @st, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@plan", planId);
        cmd.Parameters.AddWithValue("@eq", equipmentId);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    // POM_LOT 1건 시드. ROUTE_STEPS는 '>' 직렬화(NOT NULL). LOT_STATE는 enum.ToString() 패리티. IS_HOLD는 'N'.
    private void SeedLot(string lotId, string plantId, string? workOrderId, string lotState)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
             ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES (@id, @plant, @wo, 'PROD01', 10, 0, @st, 'Idle', 'CUT>ASSY', 0, 'N', 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", lotId);
        cmd.Parameters.AddWithValue("@plant", plantId);
        cmd.Parameters.AddWithValue("@wo", (object?)workOrderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", lotState);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/POM.PlansByPlant",
            new Dictionary<string, object> { ["plantId"] = "ANY" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task PlansByPlant_returns_seeded_plans()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        SeedPlan($"PL_{Suffix()}", plant, "Draft", DateTime.UtcNow, DateTime.UtcNow.AddDays(1));
        SeedPlan($"PL_{Suffix()}", plant, "Released", DateTime.UtcNow, DateTime.UtcNow.AddDays(2));

        var rows = await Query("POM.PlansByPlant", new() { ["plantId"] = plant });
        rows.Should().HaveCount(2, "해당 공장의 계획 2건이 라운드트립돼야 한다");
        rows.Should().OnlyContain(r => r.ContainsKey("PLANNED_QTY"));
    }

    [Fact]
    public async Task PlansByPlant_status_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        SeedPlan($"PL_{Suffix()}", plant, "Draft", DateTime.UtcNow, DateTime.UtcNow.AddDays(1));
        SeedPlan($"PL_{Suffix()}", plant, "Completed", DateTime.UtcNow, DateTime.UtcNow.AddDays(2));

        var rows = await Query("POM.PlansByPlant", new() { ["plantId"] = plant, ["status"] = "Completed" });
        rows.Should().ContainSingle("Completed 필터는 1건만 반환해야 한다");
        rows[0]["STATUS"].ToString().Should().Be("Completed");
    }

    [Fact]
    public async Task PlanCountByStatus_returns_count()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        SeedPlan($"PL_{Suffix()}", plant, "Draft", DateTime.UtcNow, DateTime.UtcNow.AddDays(1));

        var rows = await Query("POM.PlanCountByStatus", new() { ["plantId"] = plant, ["status"] = "Draft" });
        var draft = rows.SingleOrDefault(r => r.ContainsKey("STATUS") && r["STATUS"].ToString() == "Draft");
        draft.Should().NotBeNull("Draft 상태 그룹 행이 반환돼야 한다");
        int.Parse(draft!["CNT"].ToString()!).Should().Be(1);
    }

    [Fact]
    public async Task OrdersByPlan_returns_seeded_orders()
    {
        EnsureSchemaReady();
        var planId = $"PL_{Suffix()}";
        SeedOrder($"WO_{Suffix()}", planId, "EQ_" + Suffix(), "Issued");
        SeedOrder($"WO_{Suffix()}", planId, "EQ_" + Suffix(), "InProgress");

        var rows = await Query("POM.OrdersByPlan", new() { ["planId"] = planId });
        rows.Should().HaveCount(2, "해당 계획의 오더 2건이 라운드트립돼야 한다");
        rows.Should().OnlyContain(r => r.ContainsKey("ORDER_QTY"));
    }

    [Fact]
    public async Task OrdersByEquipment_status_filter_narrows()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedOrder($"WO_{Suffix()}", $"PL_{Suffix()}", eq, "Issued");
        SeedOrder($"WO_{Suffix()}", $"PL_{Suffix()}", eq, "Completed");

        var rows = await Query("POM.OrdersByEquipment", new() { ["equipmentId"] = eq, ["status"] = "Completed" });
        rows.Should().ContainSingle("Completed 필터는 1건만 반환해야 한다");
        rows[0]["STATUS"].ToString().Should().Be("Completed");
    }

    [Fact]
    public async Task LotsByPlant_and_state_filter_roundtrip()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var queuedId = $"LOT_{Suffix()}";
        SeedLot(queuedId, plant, null, "Queued");
        SeedLot($"LOT_{Suffix()}", plant, null, "Completed");

        var all = await Query("POM.LotsByPlant", new() { ["plantId"] = plant });
        all.Select(r => r["LOT_ID"].ToString()).Should().Contain(queuedId);
        all.Should().HaveCount(2);

        var queued = await Query("POM.LotsByPlant", new() { ["plantId"] = plant, ["lotState"] = "Queued" });
        queued.Should().ContainSingle("Queued 필터는 1건만");
        queued[0]["LOT_STATE"].ToString().Should().Be("Queued");
        queued[0]["ROUTE_STEPS"].ToString().Should().Be("CUT>ASSY");
    }

    [Fact]
    public async Task LotsByWorkOrder_returns_lots_of_workorder()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var wo = $"WO_{Suffix()}";
        SeedLot($"LOT_{Suffix()}", plant, wo, "Queued");
        SeedLot($"LOT_{Suffix()}", plant, wo, "Processing");
        SeedLot($"LOT_{Suffix()}", plant, null, "Queued"); // 다른(미연결) Lot — 제외돼야 한다

        var rows = await Query("POM.LotsByWorkOrder", new() { ["workOrderId"] = wo });
        rows.Should().HaveCount(2, "해당 작업지시에 연결된 Lot 2건만 반환돼야 한다");
        rows.Should().OnlyContain(r => r["WORK_ORDER_ID"].ToString() == wo);
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
