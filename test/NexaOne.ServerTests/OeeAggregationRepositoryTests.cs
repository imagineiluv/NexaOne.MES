using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
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
    private OeeAggregationRepository Repo()
    {
        var provider = _factory.Services.GetRequiredService<IDatabaseProvider>();
        var ds = new EesDataSource { Provider = provider, ConnectionString = _factory.ConnString };
        return new OeeAggregationRepository(ds);
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
    public async Task AggregateWindow_computes_oee_from_state_history_and_lots()
    {
        _ = _factory.CreateClient(); // 스키마 + dev 시드
        var start = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        // EQ01: RUN 480 + DOWN 120 + RUN 360 + IDLE 480(비계획). 가동 840 / 비가동 120 / 계획 960.
        SeedHistory("EQ01", "IDLE", "RUN", start);
        SeedHistory("EQ01", "RUN", "DOWN", start.AddHours(8));
        SeedHistory("EQ01", "DOWN", "RUN", start.AddHours(10));
        SeedHistory("EQ01", "RUN", "IDLE", start.AddHours(16));
        SeedLot("LOTA_20260315", "EQ01", 600m, 30m, start.AddHours(9));
        SeedLot("LOTB_20260315", "EQ01", 400m, 20m, start.AddHours(15));

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
    public async Task AggregateDay_uses_shift_windows_and_shift_planned_time()
    {
        _ = _factory.CreateClient();
        var day = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // DAY 작업조(08:00~20:00, 720분) 내내 RUN. 계획시간=작업조 길이 720, 가동 720 → 가용성 1.0.
        SeedHistory("EQ01", "IDLE", "RUN", day.AddHours(8));
        SeedHistory("EQ01", "RUN", "IDLE", day.AddHours(20));
        SeedLot("LOT_DAY_20260510", "EQ01", 1440m, 0m, day.AddHours(12)); // 성능=(30×1440)/(720×60)=1.0

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
        SeedLot("LOTC_20260420", "EQ01", 500m, 25m, start.AddHours(4));

        var repo = Repo();
        await repo.AggregateWindowAsync(start, start.AddDays(1));
        await repo.AggregateWindowAsync(start, start.AddDays(1)); // 재실행 — delete+insert 멱등

        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EST_OEE_SUMMARY WHERE OEE_ID = 'AGG_EQ01_20260420_ALLDAY'";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(1, "재실행해도 윈도당 1행만 유지돼야 한다(멱등)");
    }
}
