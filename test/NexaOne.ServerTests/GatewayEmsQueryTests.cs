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

/// <summary>게이트웨이 우선 EMS read E2E(S5) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// EMS_WORK_ORDER / EMS_MAINTENANCE_PLAN / EMS_SPARE_PART 를 SqliteConnection 직접 INSERT로 시드한 뒤
/// 명명 read 쿼리(WorkOrdersByEquipment / WorkOrderCountByStatus / MaintenancePlansByEquipment /
/// MaintenancePlansDue / SparePartsAll / SparePartsLowStock) 라운드트립을 검증한다. + 미인증 401.
/// 실재 컬럼/NOT NULL(감사 컬럼 명시)·STATUS enum.ToString() 패리티를 충족한다.</summary>
public sealed class GatewayEmsQueryTests : IClassFixture<GatewayEmsQueryTests.EmsFactory>
{
    private const string Secret = "s5-ems-gateway-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-ems-test";
    private readonly EmsFactory _factory;
    public GatewayEmsQueryTests(EmsFactory factory) => _factory = factory;

    public sealed class EmsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-ems-e2e-{Guid.NewGuid():N}.db");
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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "ems-e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    // EMS_WORK_ORDER 1건 시드(실재 컬럼·NOT NULL 충족; 감사 컬럼 명시). STATUS는 enum.ToString() 패리티.
    private void SeedWorkOrder(string woId, string equipmentId, string status)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO EMS_WORK_ORDER
            (WO_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @eq, 'PM', '점검', 'U1', @now, @st, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", woId);
        cmd.Parameters.AddWithValue("@eq", equipmentId);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    // EMS_MAINTENANCE_PLAN 1건 시드. scheduledDate는 'yyyy-MM-dd HH:mm:ss'(SQLite TEXT 비교 호환).
    private void SeedPlan(string planId, string equipmentId, string status, DateTime scheduledDate)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO EMS_MAINTENANCE_PLAN
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

    // EMS_SPARE_PART 1건 시드.
    private void SeedPart(string partId, string partName, decimal current, decimal min, decimal max)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO EMS_SPARE_PART
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
        var res = await client.PostAsJsonAsync("/api/v1/query/EMS.WorkOrdersByEquipment",
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

        var rows = await Query("EMS.WorkOrdersByEquipment", new() { ["equipmentId"] = eq });
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

        var rows = await Query("EMS.WorkOrdersByEquipment", new() { ["equipmentId"] = eq, ["status"] = "Completed" });
        rows.Should().ContainSingle("Completed 필터는 1건만 반환해야 한다");
        rows[0]["STATUS"].ToString().Should().Be("Completed");
    }

    [Fact]
    public async Task WorkOrderCountByStatus_returns_count()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        SeedWorkOrder($"WO_{Suffix()}", eq, "Issued");

        var rows = await Query("EMS.WorkOrderCountByStatus", new() { ["status"] = "Issued", ["equipmentId"] = eq });
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

        var rows = await Query("EMS.MaintenancePlansByEquipment", new() { ["equipmentId"] = eq });
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

        var rows = await Query("EMS.MaintenancePlansDue",
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

        var all = await Query("EMS.SparePartsAll", new());
        all.Select(r => r["PART_ID"].ToString()).Should().Contain(new[] { lowId, okId });

        var low = await Query("EMS.SparePartsLowStock", new());
        var lowIds = low.Select(r => r["PART_ID"].ToString()).ToList();
        lowIds.Should().Contain(lowId, "저재고 부품은 LowStock에 포함");
        lowIds.Should().NotContain(okId, "충분 재고 부품은 LowStock에서 제외");
    }

    [Fact]
    public async Task WorkOrderList_returns_all_without_equipment_filter()
    {
        EnsureSchemaReady();
        var idA = $"WO_{Suffix()}";
        var idB = $"WO_{Suffix()}";
        SeedWorkOrder(idA, "EQ_" + Suffix(), "Issued");      // 서로 다른 설비
        SeedWorkOrder(idB, "EQ_" + Suffix(), "Completed");

        var rows = await Query("EMS.WorkOrderList", new());  // 파라미터 없음 → NULL-guard 전체조회
        var ids = rows.Select(r => r["WO_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "설비 필터 없이 전체 작업지시가 조회돼야 한다(점등용 전체조회)");
    }

    [Fact]
    public async Task MaintenancePlanList_returns_all_without_equipment_filter()
    {
        EnsureSchemaReady();
        var idA = $"PL_{Suffix()}";
        var idB = $"PL_{Suffix()}";
        SeedPlan(idA, "EQ_" + Suffix(), "Planned", DateTime.UtcNow.AddDays(7));
        SeedPlan(idB, "EQ_" + Suffix(), "Completed", DateTime.UtcNow.AddDays(-3));

        var rows = await Query("EMS.MaintenancePlanList", new());  // 파라미터 없음 → NULL-guard 전체조회
        var ids = rows.Select(r => r["PLAN_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "설비 필터 없이 전체 보전계획이 조회돼야 한다(점등용 전체조회)");
    }

    // V036 점검항목 마스터 시드(FK는 Foreign Keys=False라 무시; NOT NULL: 이름/감사).
    private void Exec(string sql, Action<Microsoft.Data.Sqlite.SqliteCommand> bind)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task MaintItemClassList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"MIC_{Suffix()}";
        Exec(@"INSERT INTO EMS_MAINT_ITEM_CLASS (ITEM_CLASS_ID, ITEM_CLASS_NAME, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, '점검그룹', 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        });

        var rows = await Query("EMS.MaintItemClassList", new());
        rows.Select(r => r["ITEM_CLASS_ID"].ToString()).Should().Contain(id, "V036 점검항목 그룹이 전체조회돼야 한다");
    }

    [Fact]
    public async Task MaintItemList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"MI_{Suffix()}";
        Exec(@"INSERT INTO EMS_MAINT_ITEM (ITEM_ID, ITEM_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, '점검항목', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        });

        var rows = await Query("EMS.MaintItemList", new());
        rows.Select(r => r["ITEM_ID"].ToString()).Should().Contain(id, "V036 점검항목이 전체조회돼야 한다");
    }

    [Fact]
    public async Task EqpMaintItemList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"EMI_{Suffix()}";
        Exec(@"INSERT INTO EMS_EQP_MAINT_ITEM (EQP_ITEM_ID, EQUIPMENT_ID, ITEM_ID, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, @eq, @item, 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@eq", "EQ_" + Suffix());
            cmd.Parameters.AddWithValue("@item", "MI_" + Suffix());
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        });

        var rows = await Query("EMS.EqpMaintItemList", new());
        rows.Select(r => r["EQP_ITEM_ID"].ToString()).Should().Contain(id, "V036 설비별 점검항목이 전체조회돼야 한다");
    }

    [Fact]
    public async Task SparePartClassList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"PC_{Suffix()}";
        Exec(@"INSERT INTO EMS_SPARE_PART_CLASS (PART_CLASS_ID, PART_CLASS_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, '베어링류', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")); });
        var rows = await Query("EMS.SparePartClassList", new());
        rows.Select(r => r["PART_CLASS_ID"].ToString()).Should().Contain(id, "V045 예비품 그룹이 전체조회돼야 한다");
    }

    [Fact]
    public async Task SparePartIncomingList_returns_only_incoming()
    {
        EnsureSchemaReady();
        var inc = $"IO_{Suffix()}";
        var scr = $"IO_{Suffix()}";
        Exec(@"INSERT INTO EMS_SPARE_PART_INOUT (INOUT_ID, PART_ID, TRANSACTION_TYPE, TRANSACTION_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, 'P1', 'Incoming', @now, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", inc); cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")); });
        Exec(@"INSERT INTO EMS_SPARE_PART_INOUT (INOUT_ID, PART_ID, TRANSACTION_TYPE, TRANSACTION_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, 'P1', 'Scrap', @now, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", scr); cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")); });

        var rows = await Query("EMS.SparePartIncomingList", new());
        var ids = rows.Select(r => r["INOUT_ID"].ToString()).ToList();
        ids.Should().Contain(inc, "입고가 조회돼야 한다");
        ids.Should().NotContain(scr, "폐기는 입고 쿼리에서 제외돼야 한다(TRANSACTION_TYPE 고정 필터)");
    }

    [Fact]
    public async Task SparePartInoutList_returns_all_types()
    {
        EnsureSchemaReady();
        var id = $"IO_{Suffix()}";
        Exec(@"INSERT INTO EMS_SPARE_PART_INOUT (INOUT_ID, PART_ID, TRANSACTION_TYPE, TRANSACTION_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, 'P1', 'Move', @now, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")); });
        var rows = await Query("EMS.SparePartInoutList", new());
        rows.Select(r => r["INOUT_ID"].ToString()).Should().Contain(id, "V045 입출고 전체조회에 포함돼야 한다");
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
