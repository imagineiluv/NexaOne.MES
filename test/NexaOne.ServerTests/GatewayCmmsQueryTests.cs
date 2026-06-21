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

/// <summary>게이트웨이 우선 CMMS read E2E(S5) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// CMMS_WORK_ORDER / CMMS_MAINTENANCE_PLAN / CMMS_SPARE_PART 를 SqliteConnection 직접 INSERT로 시드한 뒤
/// 명명 read 쿼리(WorkOrdersByEquipment / WorkOrderCountByStatus / MaintenancePlansByEquipment /
/// MaintenancePlansDue / SparePartsAll / SparePartsLowStock) 라운드트립을 검증한다. + 미인증 401.
/// 실재 컬럼/NOT NULL(감사 컬럼 명시)·STATUS enum.ToString() 패리티를 충족한다.</summary>
public sealed class GatewayCmmsQueryTests : IClassFixture<GatewayCmmsQueryTests.CmmsFactory>
{
    private const string Secret = "s5-cmms-gateway-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-cmms-test";
    private readonly CmmsFactory _factory;
    public GatewayCmmsQueryTests(CmmsFactory factory) => _factory = factory;

    public sealed class CmmsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-cmms-e2e-{Guid.NewGuid():N}.db");
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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "cmms-e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    // CMMS_WORK_ORDER 1건 시드(실재 컬럼·NOT NULL 충족; 감사 컬럼 명시). STATUS는 enum.ToString() 패리티.
    private void SeedWorkOrder(string woId, string equipmentId, string status)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO CMMS_WORK_ORDER
            (WO_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @eq, 'PM', '점검', 'U1', @now, @st, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", woId);
        cmd.Parameters.AddWithValue("@eq", equipmentId);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    // CMMS_MAINTENANCE_PLAN 1건 시드. scheduledDate는 'yyyy-MM-dd HH:mm:ss'(SQLite TEXT 비교 호환).
    private void SeedPlan(string planId, string equipmentId, string status, DateTime scheduledDate)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO CMMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE, SCHEDULED_DATE,
             ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, '월간점검', @eq, 'PM', 'Monthly', @sched, 2.5, 'U1', @st, 'TEST', @now, 'TEST', @now)";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        cmd.Parameters.AddWithValue("@id", planId);
        cmd.Parameters.AddWithValue("@eq", equipmentId);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@sched", scheduledDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    // CMMS_SPARE_PART 1건 시드.
    private void SeedPart(string partId, string partName, decimal current, decimal min, decimal max)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO CMMS_SPARE_PART
            (PART_ID, PART_NAME, PART_NUMBER, DESCRIPTION, UNIT_OF_MEASURE,
             CURRENT_STOCK, MIN_STOCK, MAX_STOCK, LOCATION,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @name, 'PN-001', '설명', 'EA', @cur, @min, @max, 'A-1', 'TEST', @now, 'TEST', @now)";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        cmd.Parameters.AddWithValue("@id", partId);
        cmd.Parameters.AddWithValue("@name", partName);
        cmd.Parameters.AddWithValue("@cur", current);
        cmd.Parameters.AddWithValue("@min", min);
        cmd.Parameters.AddWithValue("@max", max);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/CMMS.WorkOrdersByEquipment",
            new Dictionary<string, object> { ["equipmentId"] = "ANY" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task WorkOrdersByEquipment_returns_seeded_orders()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedWorkOrder($"WO_{Suffix()}", eq, "Issued");
        SeedWorkOrder($"WO_{Suffix()}", eq, "InProgress");

        var rows = await Query("CMMS.WorkOrdersByEquipment", new() { ["equipmentId"] = eq });
        rows.Should().HaveCount(2, "해당 설비의 작업지시 2건이 라운드트립돼야 한다");
        rows.Should().OnlyContain(r => r.ContainsKey("WO_TYPE"), "작업지시 행은 WO_TYPE을 포함해야 한다");
    }

    [Fact]
    public async Task WorkOrdersByEquipment_status_filter_narrows()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedWorkOrder($"WO_{Suffix()}", eq, "Issued");
        SeedWorkOrder($"WO_{Suffix()}", eq, "Completed");

        var rows = await Query("CMMS.WorkOrdersByEquipment", new() { ["equipmentId"] = eq, ["status"] = "Completed" });
        rows.Should().ContainSingle("Completed 필터는 1건만 반환해야 한다");
        rows[0]["STATUS"].ToString().Should().Be("Completed");
    }

    [Fact]
    public async Task WorkOrderCountByStatus_returns_count()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedWorkOrder($"WO_{Suffix()}", eq, "Issued");

        var rows = await Query("CMMS.WorkOrderCountByStatus", new() { ["status"] = "Issued", ["equipmentId"] = eq });
        var issued = rows.SingleOrDefault(r => r.ContainsKey("STATUS") && r["STATUS"].ToString() == "Issued");
        issued.Should().NotBeNull("Issued 상태 그룹 행이 반환돼야 한다");
        int.Parse(issued!["CNT"].ToString()!).Should().Be(1);
    }

    [Fact]
    public async Task MaintenancePlansByEquipment_returns_seeded_plans()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedPlan($"PL_{Suffix()}", eq, "Planned", DateTime.UtcNow.AddDays(7));

        var rows = await Query("CMMS.MaintenancePlansByEquipment", new() { ["equipmentId"] = eq });
        rows.Should().ContainSingle("해당 설비의 계획 1건이 라운드트립돼야 한다");
        rows[0].Should().ContainKey("CYCLE_TYPE");
        rows[0]["CYCLE_TYPE"].ToString().Should().Be("Monthly");
    }

    [Fact]
    public async Task MaintenancePlansDue_returns_only_due_and_active()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var dueId = $"PL_{Suffix()}";
        SeedPlan(dueId, eq, "Planned", DateTime.UtcNow.AddDays(-1));          // 도래 + 활성 → 포함
        SeedPlan($"PL_{Suffix()}", eq, "Completed", DateTime.UtcNow.AddDays(-2)); // 도래이나 완료 → 제외
        SeedPlan($"PL_{Suffix()}", eq, "Planned", DateTime.UtcNow.AddDays(30));   // 활성이나 미도래 → 제외

        var rows = await Query("CMMS.MaintenancePlansDue",
            new() { ["asOf"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") });
        rows.Should().Contain(r => r["PLAN_ID"].ToString() == dueId, "도래+활성 계획은 포함돼야 한다");
        rows.Should().OnlyContain(r => r["STATUS"].ToString() != "Completed" && r["STATUS"].ToString() != "Cancelled",
            "완료/취소 계획은 도래 목록에서 제외된다");
    }

    [Fact]
    public async Task SparePartsAll_and_LowStock_roundtrip()
    {
        EnsureSchemaReady();
        var lowId = $"SP_{Suffix()}";
        var okId = $"SP_{Suffix()}";
        SeedPart(lowId, "low-" + lowId, current: 2m, min: 5m, max: 50m);   // 저재고(current<=min)
        SeedPart(okId, "ok-" + okId, current: 30m, min: 5m, max: 50m);     // 충분

        var all = await Query("CMMS.SparePartsAll", new());
        all.Select(r => r["PART_ID"].ToString()).Should().Contain(new[] { lowId, okId });

        var low = await Query("CMMS.SparePartsLowStock", new());
        var lowIds = low.Select(r => r["PART_ID"].ToString()).ToList();
        lowIds.Should().Contain(lowId, "저재고 부품은 LowStock에 포함");
        lowIds.Should().NotContain(okId, "충분 재고 부품은 LowStock에서 제외");
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
