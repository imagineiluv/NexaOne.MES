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
        if (permissions.Length == 0)
            claims.Add(new Claim(NexaOne.Common.Security.Permissions.ClaimType, "pom:read"));
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

    private void SeedWorkOrder(string workOrderId, string productionOrderId, string plantId, string status)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_WORK_ORDER
            (WORK_ORDER_ID, PRODUCTION_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCT_ID,
             PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY, STATUS, IS_HOLD,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @parent, @plant, '테스트 작업지시', 'PROD01',
                    50, 0, 0, 0, @status, 'N', 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", workOrderId);
        cmd.Parameters.AddWithValue("@parent", productionOrderId);
        cmd.Parameters.AddWithValue("@plant", plantId);
        cmd.Parameters.AddWithValue("@status", status);
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

    // POM_LOT 1건 시드(수량/불량/홀드 지정) — WPM 점등 쿼리(LotList/Hold/Defect/Yield) 검증용.
    private void SeedLotFull(string lotId, string plantId, string productId, decimal qty, decimal defectQty, string lotState, string isHold)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES (@id, @plant, @prod, @qty, @def, @st, 'Idle', 'CUT>ASSY', 0, @hold, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", lotId);
        cmd.Parameters.AddWithValue("@plant", plantId);
        cmd.Parameters.AddWithValue("@prod", productId);
        cmd.Parameters.AddWithValue("@qty", qty);
        cmd.Parameters.AddWithValue("@def", defectQty);
        cmd.Parameters.AddWithValue("@st", lotState);
        cmd.Parameters.AddWithValue("@hold", isHold);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    // POM_LOT_HISTORY 1건 시드(LOT_HISTORY_ID는 IDENTITY→자동). LOT 추적(LotTraceList) 검증용.
    private void SeedLotHistory(string plantId, string lotId, string equipmentId, string executionId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_LOT_HISTORY
            (PLANT_ID, LOT_ID, EQUIPMENT_ID, PROCESS_ID, TRACK_IN_TIME, EXECUTION_ID, EXECUTION_USER, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE)
            VALUES (@plant, @lot, @eq, 'PROC1', @now, @exec, 'TEST', 10, 0, 'Processing', 'Run')";
        cmd.Parameters.AddWithValue("@plant", plantId);
        cmd.Parameters.AddWithValue("@lot", lotId);
        cmd.Parameters.AddWithValue("@eq", equipmentId);
        cmd.Parameters.AddWithValue("@exec", executionId);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
    }

    private void SeedRouteExceptionAndExecution(
        string plantId,
        string lotId,
        string exceptionId,
        DateTime? expiresAt = null)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO POM_ROUTE_EXCEPTION
            (EXCEPTION_ID, LOT_ID, PLANT_ID, DEVIATION_TYPE, FROM_STEP, TO_STEP,
             FROM_PROCESS_ID, TO_PROCESS_ID, BOUND_LOT_VERSION, REASON, STATUS,
             REQUESTED_BY, REQUESTED_AT, EXPIRES_AT, CLIENT_CHANNEL, DEVICE_ID, CREATED_AT, UPDATED_AT)
            VALUES (@exception, @lot, @plant, 'Bypass', 0, 1, 'CUT', 'ASSY', 1,
                    '설비 고장', 'Requested', 'operator', @now, @expires, 'MOBILE', 'PDA-07', @now, @now);
            INSERT INTO POM_LOT_EXECUTION
            (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
             EXPECTED_VERSION, RESULT_VERSION, FROM_STEP, TO_STEP,
             FROM_PROCESS_ID, TO_PROCESS_ID, CONTROL_MODE, CLIENT_CHANNEL, DEVICE_ID, REASON,
             CREATED_BY, CREATED_AT)
            VALUES (@execution, @lot, 'SequenceChange', @key, @hash,
                    1, 2, 0, 1, 'CUT', 'ASSY', 'NoControl', 'POP', 'KIOSK-03',
                    '병목 우회', 'operator', @now)";
        cmd.Parameters.AddWithValue("@exception", exceptionId);
        cmd.Parameters.AddWithValue("@execution", $"EXEC-{exceptionId}");
        cmd.Parameters.AddWithValue("@key", $"KEY-{exceptionId}");
        cmd.Parameters.AddWithValue("@hash", new string('a', 64));
        cmd.Parameters.AddWithValue("@lot", lotId);
        cmd.Parameters.AddWithValue("@plant", plantId);
        var expiration = expiresAt ?? DateTime.UtcNow.AddMinutes(30);
        cmd.Parameters.AddWithValue("@now", expiration.AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@expires", expiration.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private void SeedLotDefectExecution(string plantId, string lotId, string exceptionId)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO POM_LOT_DEFECT_EXECUTION
            (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
             EXECUTION_USER, CLIENT_CHANNEL, DEVICE_ID, OCCURRED_AT, CREATED_AT)
            VALUES (@execution, @lot, @plant, 'ASSY', 'SCRATCH', 1.5,
                    'operator', 'POP', 'KIOSK-03', @now, @now)";
        command.Parameters.AddWithValue("@execution", $"EXEC-{exceptionId}");
        command.Parameters.AddWithValue("@lot", lotId);
        command.Parameters.AddWithValue("@plant", plantId);
        command.Parameters.AddWithValue("@now", Now());
        command.ExecuteNonQuery();
    }

    private void SeedAutomaticReturnExecution(string plantId, string lotId, string correlationId)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO POM_LOT_EXECUTION
            (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
             EXPECTED_VERSION, RESULT_VERSION, FROM_STEP, TO_STEP,
             FROM_PROCESS_ID, TO_PROCESS_ID, CONTROL_MODE, CLIENT_CHANNEL, DEVICE_ID, REASON,
             CREATED_BY, CREATED_AT)
            VALUES (@execution, @lot, 'TrackOut', @key, @hash,
                    2, 3, 1, 2, 'REWORK', 'ASSY', 'Flexible', 'MOBILE', 'PDA-07',
                    'TrackOut and automatic rework Return', 'rework-operator', @now);
            INSERT INTO POM_LOT_HISTORY
            (PLANT_ID, LOT_ID, EQUIPMENT_ID, PROCESS_ID, TRACK_IN_TIME, TRACK_OUT_TIME,
             EXECUTION_ID, EXECUTION_USER, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
             REASON, IDEMPOTENCY_KEY)
            VALUES (@plant, @lot, 'EQ-REWORK', 'ASSY', @now, @now,
                    'Return', 'rework-operator', 10, 0, 'Processing', 'Idle',
                    'Automatic return after rework TrackOut: REWORK -> ASSY', @execution)";
        command.Parameters.AddWithValue("@execution", $"RETURN-{correlationId}");
        command.Parameters.AddWithValue("@key", $"TRACKOUT-{correlationId}");
        command.Parameters.AddWithValue("@hash", new string('b', 64));
        command.Parameters.AddWithValue("@plant", plantId);
        command.Parameters.AddWithValue("@lot", lotId);
        command.Parameters.AddWithValue("@now", Now());
        command.ExecuteNonQuery();
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
        var plant = "PLANT01";
        var plan = $"PL_{Suffix()}";
        var productionOrder = $"PO_{Suffix()}";
        var wo = $"WO_{Suffix()}";
        var start = DateTime.UtcNow;
        SeedPlan(plan, plant, "Released", start, start.AddDays(1));
        SeedOrder(productionOrder, plan, "EQ01", "Issued");
        SeedWorkOrder(wo, productionOrder, plant, "Released");
        SeedLot($"LOT_{Suffix()}", plant, wo, "Queued");
        SeedLot($"LOT_{Suffix()}", plant, wo, "Processing");
        SeedLot($"LOT_{Suffix()}", plant, null, "Queued"); // 다른(미연결) Lot — 제외돼야 한다

        var rows = await Query("POM.LotsByWorkOrder", new() { ["workOrderId"] = wo });
        rows.Should().HaveCount(2, "해당 작업지시에 연결된 Lot 2건만 반환돼야 한다");
        rows.Should().OnlyContain(r => r["WORK_ORDER_ID"].ToString() == wo);
    }

    [Fact]
    public async Task ProductionOrderList_returns_all_without_plan_filter()
    {
        EnsureSchemaReady();
        var idA = $"PO_{Suffix()}";
        var idB = $"PO_{Suffix()}";
        var planA = $"PL_{Suffix()}";
        var planB = $"PL_{Suffix()}";
        var start = DateTime.UtcNow;
        SeedPlan(planA, "PLANT01", "Released", start, start.AddDays(1));
        SeedPlan(planB, "PLANT01", "Completed", start, start.AddDays(1));
        SeedOrder(idA, planA, "EQ01", "Issued");
        SeedOrder(idB, planB, "EQ02", "Completed");

        var rows = await Query("POM.ProductionOrderList", new());  // 필터 없음 → NULL-guard 전체조회
        var ids = rows.Select(r => r["ORDER_ID"].ToString()).ToList();
        ids.Should().Contain(new[] { idA, idB }, "계획/설비 필터 없이 전체 생산오더가 조회돼야 한다(점등용 전체조회)");
    }

    // ===== SmartUX WPM(작업진행)·RPT 점등(신설 전체조회) — LotList/Hold/Defect/Yield/Trace. =====

    [Fact]
    public async Task LotList_returns_all_and_hold_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var held = $"LOT_{Suffix()}";
        var free = $"LOT_{Suffix()}";
        SeedLotFull(held, plant, "PRODX", 10m, 0m, "Processing", "Y");
        SeedLotFull(free, plant, "PRODX", 20m, 0m, "Processing", "N");

        var all = await Query("POM.LotList", new() { ["plantId"] = plant });
        all.Select(r => r["LOT_ID"].ToString()).Should().Contain(new[] { held, free }, "공장 전체 Lot이 조회돼야 한다(LOT 관리 점등)");

        var heldOnly = await Query("POM.LotList", new() { ["plantId"] = plant, ["isHold"] = "Y" });
        var ids = heldOnly.Select(r => r["LOT_ID"].ToString()).ToList();
        ids.Should().Contain(held);
        ids.Should().NotContain(free, "isHold 필터는 홀드 Lot만 반환");
    }

    [Fact]
    public async Task LotHoldList_returns_only_held()
    {
        EnsureSchemaReady();
        var held = $"LOT_{Suffix()}";
        var free = $"LOT_{Suffix()}";
        SeedLotFull(held, "P_" + Suffix(), "PRODX", 10m, 0m, "Processing", "Y");
        SeedLotFull(free, "P_" + Suffix(), "PRODX", 10m, 0m, "Processing", "N");

        var rows = await Query("POM.LotHoldList", new());
        rows.Select(r => r["LOT_ID"].ToString()).Should().Contain(held).And.NotContain(free);
        rows.Should().OnlyContain(r => r["IS_HOLD"].ToString() == "Y", "홀드 상태 Lot만(LOT Hold/해제 점등)");
    }

    [Fact]
    public async Task LotDefectList_returns_only_defective()
    {
        EnsureSchemaReady();
        var bad = $"LOT_{Suffix()}";
        var good = $"LOT_{Suffix()}";
        SeedLotFull(bad, "P_" + Suffix(), "PRODX", 100m, 5m, "Completed", "N");
        SeedLotFull(good, "P_" + Suffix(), "PRODX", 100m, 0m, "Completed", "N");

        var rows = await Query("POM.LotDefectList", new());
        var ids = rows.Select(r => r["LOT_ID"].ToString()).ToList();
        ids.Should().Contain(bad);
        ids.Should().NotContain(good, "불량 수량 0 Lot은 제외(불량 수리 점등)");
    }

    [Fact]
    public async Task YieldByProduct_aggregates_qty_and_good()
    {
        EnsureSchemaReady();
        var prod = "PRD_" + Suffix();
        SeedLotFull($"LOT_{Suffix()}", "P_" + Suffix(), prod, 100m, 10m, "Completed", "N");
        SeedLotFull($"LOT_{Suffix()}", "P_" + Suffix(), prod, 200m, 20m, "Completed", "N");

        var rows = await Query("POM.YieldByProduct", new());
        var row = rows.Single(r => r["PRODUCT_ID"].ToString() == prod);
        decimal.Parse(row["TOTAL_QTY"].ToString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(300m);
        decimal.Parse(row["GOOD_QTY"].ToString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(270m, "양품 = 총생산-불량(수율 현황 점등)");
    }

    [Fact]
    public async Task LotTraceList_returns_history_without_required_filter()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var lot = $"LOT_{Suffix()}";
        SeedLotFull(lot, plant, "PROD_" + Suffix(), 10m, 0m, "Processing", "N");
        SeedLotHistory(plant, lot, "EQ_" + Suffix(), "TrackIn");

        var rows = await Query("POM.LotTraceList", new() { ["plantId"] = plant });
        rows.Select(r => r["LOT_ID"].ToString()).Should().Contain(lot, "Lot 이력이 조회돼야 한다(LOT 추적 점등)");
        rows.Should().OnlyContain(r => r.ContainsKey("EXECUTION_ID") && r.ContainsKey("QTY"));
    }

    [Fact]
    public async Task LotRoutingContextList_uses_rework_return_step_as_next_operation()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var lot = $"LOT_{Suffix()}";
        SeedLot(lot, plant, null, "Queued");

        using (var connection = new SqliteConnection(_factory.ConnString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE POM_LOT
                                    SET ROUTE_STEPS='CUT>HEAT>ASSY', CURRENT_STEP=0,
                                        RETURN_STEP=2, CONTROL_MODE='Flexible', UPDATED_AT=@now
                                    WHERE LOT_ID=@lot";
            command.Parameters.AddWithValue("@lot", lot);
            command.Parameters.AddWithValue("@now", Now());
            command.ExecuteNonQuery();
        }

        var rows = await Query("POM.LotRoutingContextList", new()
        {
            ["plantId"] = plant,
            ["lotId"] = lot,
        });

        var row = rows.Should().ContainSingle().Subject;
        row["CURRENT_PROCESS_ID"].ToString().Should().Be("CUT");
        row["NEXT_STEP"].ToString().Should().Be("2");
        row["NEXT_PROCESS_ID"].ToString().Should().Be("ASSY",
            "재작업 완료 후에는 선형 다음 공정 HEAT가 아니라 저장된 복귀점 ASSY를 안내해야 한다");
        row["RETURN_PROCESS_ID"].ToString().Should().Be("ASSY");
        row["IS_IN_REWORK"].ToString().Should().Be("Y");
    }

    [Fact]
    public async Task RouteExceptionList_projects_selection_aliases_for_review_and_apply()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var lot = $"LOT_{Suffix()}";
        var exception = $"REX-{Suffix()}";
        SeedLot(lot, plant, null, "Queued");
        SeedRouteExceptionAndExecution(plant, lot, exception);
        using (var connection = new SqliteConnection(_factory.ConnString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE POM_ROUTE_EXCEPTION
                                    SET STATUS='Approved', REVIEWED_BY='supervisor', REVIEWED_AT=@reviewedAt,
                                        REVIEW_REASON='Emergency route approved',
                                        REVIEW_CLIENT_CHANNEL='POP', REVIEW_DEVICE_ID='KIOSK-03'
                                    WHERE EXCEPTION_ID=@exception";
            command.Parameters.AddWithValue("@exception", exception);
            command.Parameters.AddWithValue("@reviewedAt", Now());
            command.ExecuteNonQuery();
        }

        var rows = await Query("POM.RouteExceptionList", new()
        {
            ["plantId"] = plant,
            ["lotId"] = lot,
            ["exceptionStatus"] = "Approved",
        });

        var row = rows.Should().ContainSingle().Subject;
        row["EXCEPTION_ID"].ToString().Should().Be(exception);
        row["TARGET_STEP_INDEX"].ToString().Should().Be("1");
        row["VERSION_NO"].ToString().Should().Be("1");
        row["FROM_PROCESS_ID"].ToString().Should().Be("CUT");
        row["TO_PROCESS_ID"].ToString().Should().Be("ASSY");
        row["CLIENT_CHANNEL"].ToString().Should().Be("MOBILE");
        row["DEVICE_ID"].ToString().Should().Be("PDA-07");
        row["REVIEW_CLIENT_CHANNEL"].ToString().Should().Be("POP");
        row["REVIEW_DEVICE_ID"].ToString().Should().Be("KIOSK-03");
    }

    [Fact]
    public async Task RouteExceptionList_projects_elapsed_requests_as_expired()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var lot = $"LOT_{Suffix()}";
        var exception = $"REX-{Suffix()}";
        SeedLot(lot, plant, null, "Queued");
        SeedRouteExceptionAndExecution(plant, lot, exception, DateTime.UtcNow.AddMinutes(-1));

        var rows = await Query("POM.RouteExceptionList", new()
        {
            ["plantId"] = plant,
            ["lotId"] = lot,
            ["exceptionStatus"] = "Expired",
        });

        rows.Should().ContainSingle();
        rows[0]["STATUS"].ToString().Should().Be("Expired");
    }

    [Fact]
    public async Task RouteDeviationTimeline_combines_deviation_execution_and_automatic_return_history()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var lot = $"LOT_{Suffix()}";
        var exception = $"REX-{Suffix()}";
        SeedLot(lot, plant, null, "Queued");
        SeedRouteExceptionAndExecution(plant, lot, exception);
        SeedAutomaticReturnExecution(plant, lot, exception);

        var rows = await Query("POM.RouteDeviationTimeline", new()
        {
            ["plantId"] = plant,
            ["lotId"] = lot,
        });

        rows.Select(row => row["ACTION"].ToString())
            .Should().Contain(new[] { "SequenceChange", "Return" });
        var returned = rows.Single(row => row["ACTION"].ToString() == "Return");
        returned["EXECUTION_ID"].ToString().Should().StartWith("HIST-");
        returned["FROM_STEP"].ToString().Should().Be("1");
        returned["TO_STEP"].ToString().Should().Be("2");
        returned["FROM_PROCESS_ID"].ToString().Should().Be("REWORK");
        returned["TO_PROCESS_ID"].ToString().Should().Be("ASSY");
        returned["CONTROL_MODE"].ToString().Should().Be("Flexible");
        returned["CLIENT_CHANNEL"].ToString().Should().Be("MOBILE");
        returned["DEVICE_ID"].ToString().Should().Be("PDA-07");
        returned["EXPECTED_VERSION"].ToString().Should().Be("2");
        returned["RESULT_VERSION"].ToString().Should().Be("3");
        returned["CREATED_BY"].ToString().Should().Be("rework-operator");
    }

    [Fact]
    public async Task LotDefectExecutionList_returns_code_level_track_out_evidence()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var lot = $"LOT_{Suffix()}";
        var exception = $"REX-{Suffix()}";
        SeedLot(lot, plant, null, "Queued");
        SeedRouteExceptionAndExecution(plant, lot, exception);
        SeedLotDefectExecution(plant, lot, exception);

        var rows = await Query("POM.LotDefectExecutionList", new()
        {
            ["plantId"] = plant,
            ["lotId"] = lot,
            ["processId"] = "ASSY",
            ["defectCode"] = "SCRATCH",
        });

        var row = rows.Should().ContainSingle().Subject;
        row["DEFECT_QTY"].ToString().Should().Be("1.5");
        row["EXECUTION_USER"].ToString().Should().Be("operator");
        row["CLIENT_CHANNEL"].ToString().Should().Be("POP");
        row["DEVICE_ID"].ToString().Should().Be("KIOSK-03");
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
