using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Est;
using NexusCom.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>OEE 집계 리포지토리 통합검증(end-to-end) — 모듈 소유 <see cref="OeeAggregationRepository"/>를 호스트가 SQLite로
/// 부트한 DB에 직접 물려(EesDataSource = 호스트 DI의 IDatabaseProvider + 팩터리 연결문자열) 집계를 검증한다.
/// dev 시드가 EST_OEE_TARGET(EQ01~03)/EST_STATE_CATEGORY/MDM_SHIFT(DAY·NIGHT)/MDM_EQUIPMENT를 채운다. 여기서 EQ01의
/// 원자료(상태전이 + POM_LOT 생산/불량)를 특정 윈도에 시드한 뒤 AggregateWindowAsync/AggregateDayAsync가 EST_OEE_SUMMARY/
/// LOSS 마트를 올바른 OEE로 적재하는지(가용성/성능/품질·유실·작업조 계획시간·멱등) 검증한다. modules OFF.</summary>
public sealed class OeeAggregationRepositoryTests : IClassFixture<OeeAggregationRepositoryTests.OeeFactory>
{
    private readonly OeeFactory _factory;
    public OeeAggregationRepositoryTests(OeeFactory factory) => _factory = factory;

    public sealed class OeeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-oee-repo-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "oee-repo-e2e-jwt-secret-key-at-least-32-bytes!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-oee-test");
            builder.UseSetting("Jwt:Audience", "nexaone-oee-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    // 스키마 부트스트랩 + dev 시드(목표/분류/작업조/설비). IDatabaseProvider(SQLite)는 호스트 DI에 등록돼 있다.
    private EesDataSource DataSource()
    {
        var provider = _factory.Services.GetRequiredService<IDatabaseProvider>();
        return new EesDataSource { Provider = provider, ConnectionString = _factory.ConnString };
    }

    private OeeAggregationRepository Repo()
    {
        var dataSource = DataSource();
        return new OeeAggregationRepository(dataSource, new OeeEvidenceSource(dataSource));
    }

    private sealed class StubEvidenceSource(OeeProductionWindowDto production) : IOeeEvidenceSource
    {
        public Task<OeePlanSnapshotDto> LoadPlanAsync(
            IReadOnlyList<string> targetEquipmentIds,
            DateTime? localDay,
            CancellationToken ct = default)
            => Task.FromResult(new OeePlanSnapshotDto(
                targetEquipmentIds
                    .Select(static equipmentId => new OeeEquipmentScopeDto(equipmentId, "PLANT01"))
                    .ToArray(),
                []));

        public Task<OeeProductionWindowDto> LoadProductionAsync(
            string plantId,
            string equipmentId,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken ct = default)
            => Task.FromResult(production);
    }

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private static string Ts(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void SeedHistory(string equipmentId, string fromState, string toState, DateTime changedAt)
        => Exec(@"INSERT INTO EST_EQUIPMENT_STATE_HISTORY
            (HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE, CHANGED_AT, CHANGED_BY, SOURCE_TYPE)
            VALUES (@id, @eq, @from, @to, @to, @at, 'TEST', 'TEST')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", $"{equipmentId}_{changedAt:yyyyMMddHHmmssfff}");
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@from", fromState);
            cmd.Parameters.AddWithValue("@to", toState);
            cmd.Parameters.AddWithValue("@at", Ts(changedAt));
        });

    private void SeedTrackOut(string lotId, string equipmentId, decimal qty, decimal defectQty, DateTime trackOut)
    {
        Exec(@"INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
             ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES (@id, 'PLANT01', 'ITEM01', @qty, @def, 'Completed', 'Idle',
                    'PROC_MACH', 1, 'N', 'TEST', @out)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", lotId);
            cmd.Parameters.AddWithValue("@qty", qty);
            cmd.Parameters.AddWithValue("@def", defectQty);
            cmd.Parameters.AddWithValue("@out", Ts(trackOut));
        });
        Exec(@"INSERT INTO POM_LOT_HISTORY
            (PLANT_ID, LOT_ID, EQUIPMENT_ID, PROCESS_ID, TRACK_OUT_TIME, EXECUTION_ID,
             EXECUTION_USER, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE, CREATED_AT)
            VALUES ('PLANT01', @id, @eq, 'PROC_MACH', @out, 'TrackOut',
                    'TEST', @qty, @def, 'Processing', 'Idle', @out)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", lotId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@qty", qty);
            cmd.Parameters.AddWithValue("@def", defectQty);
            cmd.Parameters.AddWithValue("@out", Ts(trackOut));
        });
    }

    private void SeedOutput(string eventId, string equipmentId, decimal qty, decimal defectQty, DateTime occurredAt)
        => Exec(@"INSERT INTO EST_EQUIPMENT_OUTPUT_EVENT
            (OUTPUT_EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID, OUTPUT_TYPE,
             TOTAL_QTY, GOOD_QTY, DEFECT_QTY, UNIT, SOURCE, ACTOR_ID, OCCURRED_AT, CREATED_BY, CREATED_AT,
             IS_LOT_OUTPUT)
            VALUES (@id, @key, @hash, 'PLANT01', @eq, 'CarrierCleaned',
                    @qty, @good, @def, 'EA', 'TEST', 'TEST', @at, 'TEST', @at, 0)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", eventId);
            cmd.Parameters.AddWithValue("@key", "idem:" + eventId);
            cmd.Parameters.AddWithValue("@hash", "hash:" + eventId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@qty", qty);
            cmd.Parameters.AddWithValue("@good", qty - defectQty);
            cmd.Parameters.AddWithValue("@def", defectQty);
            cmd.Parameters.AddWithValue("@at", Ts(occurredAt));
        });

    private void SeedLotOutput(
        string eventId,
        string equipmentId,
        string lotId,
        decimal qty,
        decimal defectQty,
        DateTime occurredAt)
        => Exec(@"INSERT INTO EST_EQUIPMENT_OUTPUT_EVENT
            (OUTPUT_EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID, OUTPUT_TYPE,
             PROCESS_LOT_ID, TOTAL_QTY, GOOD_QTY, DEFECT_QTY, UNIT, SOURCE, ACTOR_ID,
             OCCURRED_AT, CREATED_BY, CREATED_AT, IS_LOT_OUTPUT)
            VALUES (@id, @key, @hash, 'PLANT01', @eq, 'Lot', @lot,
                    @qty, @good, @def, 'EA', 'POM', 'TEST', @at, 'TEST', @at, 1)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", eventId);
            cmd.Parameters.AddWithValue("@key", "idem:" + eventId);
            cmd.Parameters.AddWithValue("@hash", "hash:" + eventId);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@lot", lotId);
            cmd.Parameters.AddWithValue("@qty", qty);
            cmd.Parameters.AddWithValue("@good", qty - defectQty);
            cmd.Parameters.AddWithValue("@def", defectQty);
            cmd.Parameters.AddWithValue("@at", Ts(occurredAt));
        });

    private void SeedTaktLot(string lotId)
        => Exec(@"INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE,
             ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES (@id, 'PLANT01', 'ITEM01', 400, 0, 'Completed', 'Idle',
                    'PROC_MACH', 1, 'N', 'TEST', @at)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", lotId);
            cmd.Parameters.AddWithValue("@at", Ts(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        });

    private void SeedMeasuredTrackOut(string lotId, DateTime trackIn, DateTime trackOut)
        => Exec(@"INSERT INTO POM_LOT_HISTORY
            (PLANT_ID, LOT_ID, EQUIPMENT_ID, PROCESS_ID, TRACK_IN_TIME, TRACK_OUT_TIME, EXECUTION_ID,
             EXECUTION_USER, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE, CREATED_AT)
            VALUES ('PLANT01', @id, 'EQ01', 'PROC_MACH', @in, @out, 'TrackOut',
                    'TEST', 400, 0, 'Completed', 'Idle', @out)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", lotId);
            cmd.Parameters.AddWithValue("@in", Ts(trackIn));
            cmd.Parameters.AddWithValue("@out", Ts(trackOut));
        });

    private Dictionary<string, object>? ReadTaktSummary(string targetId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT REQUIRED_QTY, ACTUAL_QTY, MEASURED_QTY, NET_AVAILABLE_SECONDS,
                                   ACTUAL_RUN_SECONDS, TARGET_TAKT_SECONDS_PER_UNIT,
                                   IDEAL_CYCLE_SECONDS_PER_UNIT, ACTUAL_CYCLE_SECONDS_PER_UNIT,
                                   DEVIATION_SECONDS_PER_UNIT, DEVIATION_RATIO, AVAILABILITY_RATIO,
                                   QUANTITY_UOM, TIME_UOM
                            FROM EST_TAKT_SUMMARY WHERE TAKT_TARGET_ID = @target";
        cmd.Parameters.AddWithValue("@target", targetId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < r.FieldCount; i++)
            values[r.GetName(i)] = r.GetValue(i);
        return values;
    }

    private long CountTaktSummaries(string targetId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EST_TAKT_SUMMARY WHERE TAKT_TARGET_ID = @target";
        cmd.Parameters.AddWithValue("@target", targetId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private Dictionary<string, object>? ReadSummary(string oeeId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT SHIFT_ID, PLANNED_MINUTES, OPERATING_MINUTES, DOWNTIME_MINUTES, TOTAL_COUNT, GOOD_COUNT,
                                   DEFECT_COUNT, AVAILABILITY, PERFORMANCE, QUALITY, OEE
                            FROM EST_OEE_SUMMARY WHERE OEE_ID = @id";
        cmd.Parameters.AddWithValue("@id", oeeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        for (int i = 0; i < r.FieldCount; i++)
            d[r.GetName(i)] = r.GetValue(i);
        return d;
    }

    private static decimal D(object v) => decimal.Parse(v.ToString()!, NumberStyles.Any, CultureInfo.InvariantCulture);

    private decimal ReadLossMinutes(string equipmentId, string category, string dayPrefix)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(SUM(LOSS_MINUTES), 0) FROM EST_OEE_LOSS
                            WHERE EQUIPMENT_ID = @eq AND LOSS_CATEGORY = @cat AND OEE_DATE LIKE @day AND LOSS_ID LIKE 'AGL_%'";
        cmd.Parameters.AddWithValue("@eq", equipmentId);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@day", dayPrefix + "%");
        return D(cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Evidence_source_returns_deep_plan_and_production_snapshots()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2028, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        SeedTrackOut("LOT_EVIDENCE_20280203", "EQ01", 42m, 2m, start.AddHours(3));
        var source = new OeeEvidenceSource(DataSource());

        var plan = await source.LoadPlanAsync(["EQ01"], start.Date);
        plan.EquipmentScopes.Should().ContainSingle(scope =>
            scope.EquipmentId == "EQ01" && scope.PlantId == "PLANT01");
        var plantDay = plan.PlantDays.Should().ContainSingle(day => day.PlantId == "PLANT01").Subject;
        plantDay.IsHoliday.Should().BeFalse();
        plantDay.Windows.Should().Contain(window =>
            window.ShiftId == "DAY" && window.PlannedMinutes == 720m);

        var production = await source.LoadProductionAsync(
            "PLANT01", "EQ01", start, start.AddDays(1));
        production.LotEventCount.Should().Be(1);
        production.LotTotalCount.Should().Be(42m);
        production.LotDefectCount.Should().Be(2m);
        production.TrackOuts.Should().ContainSingle(fact =>
            fact.ProductId == "ITEM01"
            && fact.ProcessId == "PROC_MACH"
            && fact.Qty == 42m
            && fact.QuantityUom == "EA");
    }

    [Fact]
    public async Task AggregateWindow_accepts_an_in_memory_evidence_adapter_without_foreign_table_reads()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2029, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        var repository = new OeeAggregationRepository(
            DataSource(),
            new StubEvidenceSource(new OeeProductionWindowDto(1, 8m, 1m, [])));

        await repository.AggregateWindowAsync(start, start.AddHours(8));

        var row = ReadSummary("AGG_EQ01_20290105_ALLDAY");
        row.Should().NotBeNull();
        D(row!["TOTAL_COUNT"]).Should().Be(8m);
        D(row["DEFECT_COUNT"]).Should().Be(1m);
        D(row["GOOD_COUNT"]).Should().Be(7m);
    }

    [Fact]
    public async Task AggregateWindow_computes_oee_from_state_history_and_lots()
    {
        _ = _factory.CreateClient(); // 스키마 + dev 시드
        var start = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        // EQ01: RUN 480 + DOWN 120 + RUN 360 + IDLE 480(비계획). 가동 840 / 비가동 120 / 계획 960.
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "DOWN", start.AddHours(8));
        SeedHistory("EQ01", "DOWN", "RUN", start.AddHours(10));
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(16));
        SeedTrackOut("LOTA_20260315", "EQ01", 600m, 30m, start.AddHours(9));
        SeedTrackOut("LOTB_20260315", "EQ01", 400m, 20m, start.AddHours(15));

        var written = await Repo().AggregateWindowAsync(start, start.AddDays(1));
        written.Should().BeGreaterThanOrEqualTo(1);

        var row = ReadSummary("AGG_EQ01_20260315_ALLDAY");
        row.Should().NotBeNull("EQ01 OEE 집계 행(AGG_EQ01_20260315_ALLDAY)이 적재돼야 한다");
        D(row!["OPERATING_MINUTES"]).Should().Be(840m, "가동 = RUN 480 + 360");
        D(row["DOWNTIME_MINUTES"]).Should().Be(120m, "비가동 = DOWN 120");
        D(row["PLANNED_MINUTES"]).Should().Be(960m, "계획 = 가동+비가동(비계획 IDLE 제외)");
        D(row["AVAILABILITY"]).Should().Be(0.875m, "840/960");
        D(row["TOTAL_COUNT"]).Should().Be(1000m);
        D(row["GOOD_COUNT"]).Should().Be(950m);
        D(row["QUALITY"]).Should().Be(0.95m, "950/1000");
        D(row["PERFORMANCE"]).Should().BeInRange(0.59m, 0.60m, "(30×1000)/(840×60)≈0.5952");
        D(row["OEE"]).Should().BeInRange(0.49m, 0.50m, "0.875×0.5952×0.95≈0.4948");

        ReadLossMinutes("EQ01", "Breakdown", "2026-03-15").Should().Be(120m, "DOWN 120분이 Breakdown 유실로 적재");
    }

    [Fact]
    public async Task AggregateWindow_counts_non_lot_carrier_output_from_canonical_event_ledger()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedOutput("OUT_CARRIER_001", "EQ01", 10m, 1m, start.AddHours(2));
        SeedOutput("OUT_CARRIER_002", "EQ01", 15m, 2m, start.AddHours(4));

        await Repo().AggregateWindowAsync(start, start.AddHours(8));

        var row = ReadSummary("AGG_EQ01_20261201_ALLDAY");
        row.Should().NotBeNull();
        D(row!["TOTAL_COUNT"]).Should().Be(25m);
        D(row["DEFECT_COUNT"]).Should().Be(3m);
        D(row["GOOD_COUNT"]).Should().Be(22m);
        D(row["QUALITY"]).Should().Be(0.88m);
    }

    [Fact]
    public async Task AggregateWindow_combines_non_lot_output_with_legacy_lot_without_double_counting_projected_lot()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedTrackOut("LOT_MIXED_20270102", "EQ01", 100m, 10m, start.AddHours(2));
        SeedLotOutput(
            "OUT_PROJECTED_LOT_20270102", "EQ01", "LOT_MIXED_20270102",
            100m, 10m, start.AddHours(2));
        SeedOutput("OUT_CARRIER_20270102", "EQ01", 10m, 1m, start.AddHours(4));

        await Repo().AggregateWindowAsync(start, start.AddHours(8));

        var row = ReadSummary("AGG_EQ01_20270102_ALLDAY");
        row.Should().NotBeNull();
        D(row!["TOTAL_COUNT"]).Should().Be(110m,
            "legacy LOT is authoritative during migration and non-LOT output is still included");
        D(row["DEFECT_COUNT"]).Should().Be(11m);
        D(row["GOOD_COUNT"]).Should().Be(99m);
    }

    [Fact]
    public async Task AggregateDay_uses_shift_windows_and_shift_planned_time()
    {
        _ = _factory.CreateClient();
        var day = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // DAY 작업조(08:00~20:00, 720분) 내내 RUN. 계획시간=작업조 길이 720, 가동 720 → 가용성 1.0.
        var shiftStartUtc = new DateTime(2026, 5, 9, 23, 0, 0, DateTimeKind.Utc); // Seoul 08:00
        SeedHistory("EQ01", "IDLE", "RUN", shiftStartUtc);
        SeedHistory("EQ01", "RUN", "IDLE", shiftStartUtc.AddHours(12));
        SeedTrackOut("LOT_DAY_20260510", "EQ01", 1440m, 0m, shiftStartUtc.AddHours(4));

        var written = await Repo().AggregateDayAsync(day);
        written.Should().BeGreaterThanOrEqualTo(2, "DAY·NIGHT 작업조별 행이 설비마다 적재돼야 한다");

        var row = ReadSummary("AGG_EQ01_20260510_DAY");
        row.Should().NotBeNull("DAY 작업조 집계 행이 적재돼야 한다");
        row!["SHIFT_ID"].ToString().Should().Be("DAY", "작업조 인식 집계는 SHIFT_ID를 채운다");
        D(row["PLANNED_MINUTES"]).Should().Be(720m, "계획시간 = DAY 작업조 길이(12h)");
        D(row["OPERATING_MINUTES"]).Should().Be(720m);
        D(row["AVAILABILITY"]).Should().Be(1.0m);
        D(row["QUALITY"]).Should().Be(1.0m);
        D(row["PERFORMANCE"]).Should().Be(1.0m);
        D(row["OEE"]).Should().Be(1.0m, "1.0×1.0×1.0");
    }

    [Fact]
    public async Task AggregateWindow_is_idempotent_on_rerun()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedTrackOut("LOTC_20260420", "EQ01", 500m, 25m, start.AddHours(4));

        var repo = Repo();
        await repo.AggregateWindowAsync(start, start.AddDays(1));
        await repo.AggregateWindowAsync(start, start.AddDays(1)); // 재실행 — delete+insert 멱등

        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EST_OEE_SUMMARY WHERE OEE_ID = 'AGG_EQ01_20260420_ALLDAY'";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(1, "재실행해도 윈도당 1행만 유지돼야 한다(멱등)");
    }

    [Fact]
    public async Task AggregateWindow_removes_generated_rows_for_a_deactivated_oee_target()
    {
        _ = _factory.CreateClient();
        const string taktTargetId = "TAKT_DEACTIVATED_EQ01_20300210";
        const string lotId = "LOT_DEACTIVATED_EQ01_20300210";
        var start = new DateTime(2030, 2, 10, 0, 0, 0, DateTimeKind.Utc);

        Exec(@"INSERT INTO EST_TAKT_TARGET
            (TAKT_TARGET_ID, PLANT_ID, PRODUCT_ID, PROCESS_ID, EQUIPMENT_ID, SHIFT_ID,
             EFFECTIVE_FROM, EFFECTIVE_TO, REQUIRED_QTY, NET_AVAILABLE_SECONDS,
             IDEAL_CYCLE_SECONDS_PER_UNIT, QUANTITY_UOM, TIME_UOM, DESCRIPTION,
             IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, 'PLANT01', 'ITEM01', 'PROC_MACH', 'EQ01', NULL,
                    @from, @to, 100, 28800, 30, 'EA', 's/unit', 'deactivation cleanup test',
                    1, 'TEST', @from, 'TEST', @from)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", taktTargetId);
            cmd.Parameters.AddWithValue("@from", Ts(start));
            cmd.Parameters.AddWithValue("@to", Ts(start.AddDays(1).AddSeconds(-1)));
        });
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "DOWN", start.AddHours(4));
        SeedHistory("EQ01", "DOWN", "RUN", start.AddHours(5));
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedTrackOut(lotId, "EQ01", 100m, 5m, start.AddHours(6));

        var repository = Repo();
        try
        {
            await repository.AggregateWindowAsync(start, start.AddHours(8));
            ReadSummary("AGG_EQ01_20300210_ALLDAY").Should().NotBeNull();
            ReadLossMinutes("EQ01", "Breakdown", "2030-02-10").Should().Be(60m);
            CountTaktSummaries(taktTargetId).Should().Be(1);

            Exec("UPDATE EST_OEE_TARGET SET IS_ACTIVE = 0 WHERE EQUIPMENT_ID = 'EQ01'", _ => { });

            await repository.AggregateWindowAsync(start, start.AddHours(8));

            ReadSummary("AGG_EQ01_20300210_ALLDAY").Should().BeNull(
                "a target removed from the current aggregation scope must not leave a stale OEE result");
            ReadLossMinutes("EQ01", "Breakdown", "2030-02-10").Should().Be(0m);
            CountTaktSummaries(taktTargetId).Should().Be(0);
        }
        finally
        {
            Exec("UPDATE EST_OEE_TARGET SET IS_ACTIVE = 1 WHERE EQUIPMENT_ID = 'EQ01'", _ => { });
            Exec("DELETE FROM EST_TAKT_SUMMARY WHERE TAKT_TARGET_ID = @id", cmd =>
                cmd.Parameters.AddWithValue("@id", taktTargetId));
            Exec("DELETE FROM EST_TAKT_TARGET WHERE TAKT_TARGET_ID = @id", cmd =>
                cmd.Parameters.AddWithValue("@id", taktTargetId));
        }
    }

    [Fact]
    public async Task AggregateWindow_carries_state_from_before_window_start()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start.AddMinutes(-30));
        SeedTrackOut("LOT_CARRY_20260801", "EQ01", 480m, 0m, start.AddHours(4));

        await Repo().AggregateWindowAsync(start, start.AddHours(8));

        var row = ReadSummary("AGG_EQ01_20260801_ALLDAY");
        row.Should().NotBeNull();
        D(row!["OPERATING_MINUTES"]).Should().Be(480m,
            "the RUN state active before the window must carry through when no transition occurs inside it");
        D(row["TOTAL_COUNT"]).Should().Be(480m);
    }

    [Fact]
    public async Task AggregateWindow_persists_takt_cycle_from_trackout_and_reuses_oee_availability()
    {
        _ = _factory.CreateClient();
        const string targetId = "TAKT_TEST_EQ01_20261101";
        const string lotId = "LOT_TAKT_20261101";
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        Exec(@"INSERT INTO EST_TAKT_TARGET
            (TAKT_TARGET_ID, PLANT_ID, PRODUCT_ID, PROCESS_ID, EQUIPMENT_ID, SHIFT_ID,
             EFFECTIVE_FROM, EFFECTIVE_TO, REQUIRED_QTY, NET_AVAILABLE_SECONDS,
             IDEAL_CYCLE_SECONDS_PER_UNIT, QUANTITY_UOM, TIME_UOM, DESCRIPTION,
             IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, 'PLANT01', 'ITEM01', 'PROC_MACH', 'EQ01', NULL,
                    @from, @to, 800, 28800, 30, 'EA', 's/unit', 'integration test',
                    1, 'TEST', @from, 'TEST', @from)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", targetId);
            cmd.Parameters.AddWithValue("@from", Ts(start));
            cmd.Parameters.AddWithValue("@to", Ts(start.AddDays(1).AddSeconds(-1)));
        });
        SeedTaktLot(lotId);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedMeasuredTrackOut(lotId, start, start.AddHours(4));

        var repo = Repo();
        await repo.AggregateWindowAsync(start, start.AddHours(8));
        await repo.AggregateWindowAsync(start, start.AddHours(8));

        var takt = ReadTaktSummary(targetId);
        takt.Should().NotBeNull();
        D(takt!["REQUIRED_QTY"]).Should().Be(800m);
        D(takt["ACTUAL_QTY"]).Should().Be(400m);
        D(takt["MEASURED_QTY"]).Should().Be(400m);
        D(takt["NET_AVAILABLE_SECONDS"]).Should().Be(28_800m);
        D(takt["ACTUAL_RUN_SECONDS"]).Should().Be(14_400m);
        D(takt["TARGET_TAKT_SECONDS_PER_UNIT"]).Should().Be(36m);
        D(takt["IDEAL_CYCLE_SECONDS_PER_UNIT"]).Should().Be(30m);
        D(takt["ACTUAL_CYCLE_SECONDS_PER_UNIT"]).Should().Be(36m);
        D(takt["DEVIATION_SECONDS_PER_UNIT"]).Should().Be(0m);
        D(takt["DEVIATION_RATIO"]).Should().Be(0m);
        takt["QUANTITY_UOM"].Should().Be("EA");
        takt["TIME_UOM"].Should().Be("s/unit");
        D(takt["AVAILABILITY_RATIO"]).Should().Be(D(ReadSummary("AGG_EQ01_20261101_ALLDAY")!["AVAILABILITY"]));
        CountTaktSummaries(targetId).Should().Be(1, "reaggregation must replace the generated summary atomically");
    }

    [Fact]
    public async Task AggregateDay_honors_plant_holiday_without_shift_fallback()
    {
        _ = _factory.CreateClient();
        var day = new DateTime(2026, 10, 3);
        await Repo().AggregateWindowAsync(day, day.AddDays(1));
        ReadSummary("AGG_EQ01_20261003_ALLDAY").Should().NotBeNull();
        Exec(@"INSERT INTO MDM_WORK_CALENDAR

            (CALENDAR_ID, CALENDAR_DATE, DAY_TYPE, SHIFT_ID, PLANT_ID, DESCRIPTION,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('CAL_OEE_HOLIDAY_20261003', @day, 'Holiday', NULL, 'PLANT01', 'OEE test',
                    'TEST', @day, 'TEST', @day)", cmd => cmd.Parameters.AddWithValue("@day", Ts(day)));

        await Repo().AggregateDayAsync(day);

        ReadSummary("AGG_EQ01_20261003_DAY").Should().BeNull();
        ReadSummary("AGG_EQ01_20261003_ALLDAY").Should().BeNull("holiday reaggregation must remove stale OEE rows");
        ReadSummary("AGG_EQ01_20261003_NIGHT").Should().BeNull();
        ReadSummary("AGG_EQ02_20261003_DAY").Should().BeNull();
        ReadSummary("AGG_EQ02_20261003_NIGHT").Should().BeNull();
    }

    [Fact]
    public async Task AggregateDay_removes_a_shift_that_is_no_longer_in_the_plant_calendar()
    {
        _ = _factory.CreateClient();
        var day = new DateTime(2030, 3, 11);
        var repository = Repo();

        await repository.AggregateDayAsync(day);
        ReadSummary("AGG_EQ01_20300311_DAY").Should().NotBeNull();
        ReadSummary("AGG_EQ01_20300311_NIGHT").Should().NotBeNull();

        Exec(@"INSERT INTO MDM_WORK_CALENDAR
            (CALENDAR_ID, CALENDAR_DATE, DAY_TYPE, SHIFT_ID, PLANT_ID, DESCRIPTION,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES ('CAL_OEE_DAY_ONLY_20300311', @day, 'Workday', 'DAY', 'PLANT01', 'OEE cleanup test',
                    'TEST', @day, 'TEST', @day)", cmd => cmd.Parameters.AddWithValue("@day", Ts(day)));
        try
        {
            await repository.AggregateDayAsync(day);

            ReadSummary("AGG_EQ01_20300311_DAY").Should().NotBeNull();
            ReadSummary("AGG_EQ01_20300311_NIGHT").Should().BeNull(
                "a shift removed from the authoritative day plan must not remain in the OEE mart");
        }
        finally
        {
            Exec("DELETE FROM MDM_WORK_CALENDAR WHERE CALENDAR_ID = 'CAL_OEE_DAY_ONLY_20300311'", _ => { });
        }
    }

    [Fact]
    public async Task AggregateWindow_for_one_shift_preserves_other_shift_results_on_the_same_day()
    {
        _ = _factory.CreateClient();
        var day = new DateTime(2030, 4, 12);
        var repository = Repo();

        await repository.AggregateDayAsync(day);
        ReadSummary("AGG_EQ01_20300412_DAY").Should().NotBeNull();
        ReadSummary("AGG_EQ01_20300412_NIGHT").Should().NotBeNull();

        await repository.AggregateWindowAsync(
            day, day.AddHours(8), shiftId: "DAY", plannedOverride: 480m);

        ReadSummary("AGG_EQ01_20300412_DAY").Should().NotBeNull();
        ReadSummary("AGG_EQ01_20300412_NIGHT").Should().NotBeNull(
            "a shift-scoped reaggregation must not delete another shift's completed result");
    }

    [Fact]
    public async Task Reaggregation_rolls_back_summary_when_loss_insert_fails()
    {
        _ = _factory.CreateClient();
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedTrackOut("LOT_ATOMIC_20260901", "EQ01", 400m, 0m, start.AddHours(2));

        var repo = Repo();
        await repo.AggregateWindowAsync(start, start.AddHours(8));
        var before = ReadSummary("AGG_EQ01_20260901_ALLDAY");
        before.Should().NotBeNull();
        var beforeOperating = D(before!["OPERATING_MINUTES"]);

        SeedHistory("EQ01", "RUN", "DOWN", start.AddHours(4));
        SeedHistory("EQ01", "DOWN", "RUN", start.AddHours(5));
        Exec(@"CREATE TRIGGER TR_OEE_FORCE_LOSS_FAILURE
               BEFORE INSERT ON EST_OEE_LOSS
               WHEN NEW.LOSS_ID LIKE 'AGL_EQ01_20260901_ALLDAY_%'
               BEGIN SELECT RAISE(ABORT, 'forced OEE loss failure'); END", _ => { });
        try
        {
            Func<Task> act = () => repo.AggregateWindowAsync(start, start.AddHours(8));
            await act.Should().ThrowAsync<SqliteException>();

            var after = ReadSummary("AGG_EQ01_20260901_ALLDAY");
            after.Should().NotBeNull();
            D(after!["OPERATING_MINUTES"]).Should().Be(beforeOperating,
                "summary deletion/insertion and loss insertion must roll back as one transaction");
            ReadLossMinutes("EQ01", "Breakdown", "2026-09-01").Should().Be(0m);
        }
        finally
        {
            Exec("DROP TRIGGER IF EXISTS TR_OEE_FORCE_LOSS_FAILURE", _ => { });
        }
    }
}
