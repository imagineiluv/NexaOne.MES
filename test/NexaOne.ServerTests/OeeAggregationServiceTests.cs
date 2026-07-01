using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Server.Oee;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>OEE 집계 서비스 통합검증(end-to-end) — modules OFF + SQLite. dev 시드가 EST_OEE_TARGET(EQ01~03)/
/// EST_STATE_CATEGORY/MDM_EQUIPMENT를 채운다. 여기서 EQ01의 원자료(EST_EQUIPMENT_STATE_HISTORY 상태전이 +
/// POM_LOT 생산/불량)를 특정 윈도에 시드한 뒤 <see cref="OeeAggregationService.AggregateWindowAsync"/>를 호출해
/// IRuleDispatcher(read/write) 경로로 EST_OEE_SUMMARY/LOSS 마트가 올바른 OEE로 적재되는지 검증한다.
/// 워커 산출물(AGG_/AGL_)은 데모 시드(OEE01~)와 키가 달라 분리 검증된다.</summary>
public sealed class OeeAggregationServiceTests : IClassFixture<OeeAggregationServiceTests.OeeFactory>
{
    private readonly OeeFactory _factory;
    public OeeAggregationServiceTests(OeeFactory factory) => _factory = factory;

    public sealed class OeeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-oee-agg-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "oee-agg-e2e-jwt-secret-key-at-least-32-bytes!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-oee-test");
            builder.UseSetting("Jwt:Audience", "nexaone-oee-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    private void EnsureSchemaAndSeed() => _ = _factory.CreateClient(); // 스키마 부트스트랩 + dev 시드(목표/분류/설비)

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

    // EST_EQUIPMENT_STATE_HISTORY 1건(감사 컬럼 없음). HIST_ID는 시각 기반 유니크.
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

    // POM_LOT 1건(NOT NULL: PLANT_ID/PRODUCT_ID/QTY/ROUTE_STEPS/CREATED_BY). 완료시각(TRACK_OUT_TIME)으로 윈도잉.
    private void SeedLot(string lotId, string equipmentId, decimal qty, decimal defectQty, DateTime trackOut)
        => Exec(@"INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, ROUTE_STEPS, EQUIPMENT_ID, TRACK_OUT_TIME, CREATED_BY, CREATED_AT)
            VALUES (@id, 'PLANT01', 'ITEM01', @qty, @def, 'P1', @eq, @out, 'TEST', @out)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", lotId);
            cmd.Parameters.AddWithValue("@qty", qty);
            cmd.Parameters.AddWithValue("@def", defectQty);
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@out", Ts(trackOut));
        });

    // EST_OEE_SUMMARY 단일 행의 수치 컬럼을 읽어온다(DECIMAL은 SQLite TEXT/REAL — 불변 파싱).
    private Dictionary<string, decimal>? ReadSummary(string oeeId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT PLANNED_MINUTES, OPERATING_MINUTES, DOWNTIME_MINUTES, TOTAL_COUNT, GOOD_COUNT,
                                   DEFECT_COUNT, AVAILABILITY, PERFORMANCE, QUALITY, OEE
                            FROM EST_OEE_SUMMARY WHERE OEE_ID = @id";
        cmd.Parameters.AddWithValue("@id", oeeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var d = new Dictionary<string, decimal>(StringComparer.Ordinal);
        for (int i = 0; i < r.FieldCount; i++)
            d[r.GetName(i)] = decimal.Parse(r.GetValue(i).ToString()!, NumberStyles.Any, CultureInfo.InvariantCulture);
        return d;
    }

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
        return decimal.Parse(cmd.ExecuteScalar()!.ToString()!, NumberStyles.Any, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task AggregateWindow_computes_oee_from_state_history_and_lots()
    {
        EnsureSchemaAndSeed();
        var start = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(1);

        // EQ01 상태전이: RUN 480 + DOWN 120 + RUN 360 + IDLE 480(비계획). 가동 840 / 비가동 120 / 계획 960.
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "DOWN", start.AddHours(8));
        SeedHistory("EQ01", "DOWN", "RUN", start.AddHours(10));
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(16));
        // 생산: 총 1000 / 불량 50 → 양품 950.
        SeedLot("LOTA_20260315", "EQ01", 600m, 30m, start.AddHours(9));
        SeedLot("LOTB_20260315", "EQ01", 400m, 20m, start.AddHours(15));

        var service = _factory.Services.GetRequiredService<OeeAggregationService>();
        var written = await service.AggregateWindowAsync(start, end);
        written.Should().BeGreaterThanOrEqualTo(1, "목표 등록 설비(EQ01~03)에 대해 마트 행이 적재돼야 한다");

        var row = ReadSummary("AGG_EQ01_20260315");
        row.Should().NotBeNull("EQ01 OEE 집계 행(AGG_EQ01_20260315)이 적재돼야 한다");
        row!["OPERATING_MINUTES"].Should().Be(840m, "가동 = RUN 480 + 360");
        row["DOWNTIME_MINUTES"].Should().Be(120m, "비가동 = DOWN 120");
        row["PLANNED_MINUTES"].Should().Be(960m, "계획 = 가동+비가동(비계획 IDLE 제외)");
        row["AVAILABILITY"].Should().Be(0.875m, "840/960");
        row["TOTAL_COUNT"].Should().Be(1000m);
        row["GOOD_COUNT"].Should().Be(950m);
        row["DEFECT_COUNT"].Should().Be(50m);
        row["QUALITY"].Should().Be(0.95m, "950/1000");
        row["PERFORMANCE"].Should().BeInRange(0.59m, 0.60m, "(30×1000)/(840×60)≈0.5952");
        row["OEE"].Should().BeInRange(0.49m, 0.50m, "0.875×0.5952×0.95≈0.4948");

        ReadLossMinutes("EQ01", "Breakdown", "2026-03-15").Should().Be(120m, "DOWN 구간 120분이 Breakdown 유실로 적재");
    }

    [Fact]
    public async Task AggregateWindow_is_idempotent_on_rerun()
    {
        EnsureSchemaAndSeed();
        var start = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(8));
        SeedLot("LOTC_20260420", "EQ01", 500m, 25m, start.AddHours(4));

        var service = _factory.Services.GetRequiredService<OeeAggregationService>();
        await service.AggregateWindowAsync(start, start.AddDays(1));
        await service.AggregateWindowAsync(start, start.AddDays(1)); // 재실행 — delete+insert 멱등

        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EST_OEE_SUMMARY WHERE OEE_ID = 'AGG_EQ01_20260420'";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(1, "재실행해도 윈도당 1행만 유지돼야 한다(멱등)");
    }
}
