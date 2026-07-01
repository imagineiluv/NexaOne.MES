using System.Globalization;
using NexaOne.Application.Messaging;

namespace NexaOne.Server.Oee;

/// <summary>OEE 집계 서비스(호스트 레벨) — 원자료(EST_EQUIPMENT_STATE_HISTORY 상태전이 · POM_LOT 생산/불량 수량)를
/// 설정(EST_STATE_CATEGORY · EST_OEE_TARGET)과 결합해 <see cref="OeeCalculator"/>로 계산하고 EST_OEE_SUMMARY/
/// EST_OEE_LOSS 마트에 적재한다. 모든 데이터 접근은 명명 쿼리 게이트웨이와 동일한 <see cref="IRuleDispatcher"/>
/// (provider-agnostic Dapper)로 수행한다 — SQLite/MSSQL 공통 ANSI SQL. 워커 산출물은 OEE_ID='AGG_*'/LOSS_ID='AGL_*'로
/// 키잉해 데모 시드(OEE01~/LOSS01~)를 보존하고, 윈도 재실행은 delete+insert로 멱등이다.</summary>
public sealed class OeeAggregationService
{
    private const string Ts = "yyyy-MM-dd HH:mm:ss";
    // 미분류 상태 폴백 — 계획시간엔 포함하되 가동/비가동 어느쪽도 아님(중립). 데이터 이상이 OEE를 왜곡하지 않게 한다.
    private static readonly OeeStateCategory Unknown = new("Unknown", IsProductive: false, IsDowntime: false, IsScheduled: true);

    private readonly IRuleDispatcher _dispatcher;

    public OeeAggregationService(IRuleDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>[windowStart, windowEnd) 구간의 OEE를 목표 등록 설비별로 계산·적재한다. 적재 행 수를 반환한다.</summary>
    public async Task<int> AggregateWindowAsync(DateTime windowStart, DateTime windowEnd, CancellationToken ct = default)
    {
        var fromStr = windowStart.ToString(Ts, CultureInfo.InvariantCulture);
        var toStr = windowEnd.ToString(Ts, CultureInfo.InvariantCulture);
        var dateStr = fromStr; // OEE_DATE = 윈도 시작

        var categories = await LoadCategoriesAsync(ct);
        var targets = await LoadTargetsAsync(ct);
        if (targets.Count == 0) return 0;

        // 윈도의 기존 워커 산출물 제거(멱등). 데모 시드는 다른 키라 보존된다.
        await _dispatcher.ExecuteAsync(
            "DELETE FROM EST_OEE_SUMMARY WHERE OEE_ID LIKE 'AGG_%' AND OEE_DATE >= @from AND OEE_DATE < @to",
            Args(("from", fromStr), ("to", toStr)), ct);
        await _dispatcher.ExecuteAsync(
            "DELETE FROM EST_OEE_LOSS WHERE LOSS_ID LIKE 'AGL_%' AND OEE_DATE >= @from AND OEE_DATE < @to",
            Args(("from", fromStr), ("to", toStr)), ct);

        int written = 0;
        foreach (var t in targets)
        {
            var transitions = await LoadTransitionsAsync(t.EquipmentId, fromStr, toStr, ct);
            var lots = await LoadLotCountsAsync(t.EquipmentId, fromStr, toStr, ct);
            var result = OeeCalculator.Compute(
                windowStart, windowEnd, transitions, lots,
                new OeeTarget(t.IdealCycleTimeSec, t.PlannedMinutes), categories, Unknown);

            var oeeId = $"AGG_{t.EquipmentId}_{windowStart:yyyyMMdd}";
            await _dispatcher.ExecuteAsync(InsertSummarySql, Args(
                ("id", oeeId), ("plant", t.PlantId), ("eq", t.EquipmentId), ("date", dateStr),
                ("planned", result.PlannedMinutes), ("downtime", result.DowntimeMinutes),
                ("operating", result.OperatingMinutes), ("ict", t.IdealCycleTimeSec),
                ("total", result.TotalCount), ("good", result.GoodCount), ("defect", result.DefectCount),
                ("av", result.Availability), ("pf", result.Performance), ("ql", result.Quality), ("oee", result.Oee)), ct);
            written++;

            int lossIdx = 0;
            foreach (var loss in result.Losses)
            {
                var lossId = $"AGL_{t.EquipmentId}_{windowStart:yyyyMMdd}_{lossIdx++}";
                await _dispatcher.ExecuteAsync(InsertLossSql, Args(
                    ("id", lossId), ("plant", t.PlantId), ("eq", t.EquipmentId), ("date", dateStr),
                    ("cat", loss.Category), ("min", loss.Minutes)), ct);
            }
        }
        return written;
    }

    private const string InsertSummarySql = @"
        INSERT INTO EST_OEE_SUMMARY
        (OEE_ID, PLANT_ID, EQUIPMENT_ID, OEE_DATE, SHIFT_ID,
         PLANNED_MINUTES, DOWNTIME_MINUTES, OPERATING_MINUTES, IDEAL_CYCLE_TIME_SEC,
         TOTAL_COUNT, GOOD_COUNT, DEFECT_COUNT, AVAILABILITY, PERFORMANCE, QUALITY, OEE)
        VALUES
        (@id, @plant, @eq, @date, NULL,
         @planned, @downtime, @operating, @ict,
         @total, @good, @defect, @av, @pf, @ql, @oee)";

    private const string InsertLossSql = @"
        INSERT INTO EST_OEE_LOSS
        (LOSS_ID, PLANT_ID, EQUIPMENT_ID, OEE_DATE, LOSS_CATEGORY, LOSS_MINUTES, OCCURRED_AT)
        VALUES (@id, @plant, @eq, @date, @cat, @min, @date)";

    private async Task<IReadOnlyDictionary<string, OeeStateCategory>> LoadCategoriesAsync(CancellationToken ct)
    {
        var rows = await _dispatcher.QueryAsync(
            "SELECT STATE_ID, CATEGORY, IS_PRODUCTIVE, IS_DOWNTIME, IS_SCHEDULED FROM EST_STATE_CATEGORY",
            Args(), ct);
        var map = new Dictionary<string, OeeStateCategory>(StringComparer.Ordinal);
        foreach (var r in rows)
            map[Str(r, "STATE_ID")] = new OeeStateCategory(
                Str(r, "CATEGORY"), Bool(r, "IS_PRODUCTIVE"), Bool(r, "IS_DOWNTIME"), Bool(r, "IS_SCHEDULED"));
        return map;
    }

    private async Task<List<TargetRow>> LoadTargetsAsync(CancellationToken ct)
    {
        var rows = await _dispatcher.QueryAsync(
            @"SELECT t.EQUIPMENT_ID, e.PLANT_ID, t.IDEAL_CYCLE_TIME_SEC, t.PLANNED_MINUTES
              FROM EST_OEE_TARGET t
              JOIN MDM_EQUIPMENT e ON e.EQUIPMENT_ID = t.EQUIPMENT_ID
              WHERE t.IS_ACTIVE = 1",
            Args(), ct);
        return rows.Select(r => new TargetRow(
            Str(r, "EQUIPMENT_ID"), Str(r, "PLANT_ID"),
            Dec(r, "IDEAL_CYCLE_TIME_SEC"), Dec(r, "PLANNED_MINUTES"))).ToList();
    }

    private async Task<IReadOnlyList<OeeStateTransition>> LoadTransitionsAsync(
        string equipmentId, string fromStr, string toStr, CancellationToken ct)
    {
        var rows = await _dispatcher.QueryAsync(
            @"SELECT CHANGED_AT, FROM_STATE, TO_STATE
              FROM EST_EQUIPMENT_STATE_HISTORY
              WHERE EQUIPMENT_ID = @eq AND CHANGED_AT >= @from AND CHANGED_AT < @to
              ORDER BY CHANGED_AT",
            Args(("eq", equipmentId), ("from", fromStr), ("to", toStr)), ct);
        return rows.Select(r => new OeeStateTransition(
            Date(r, "CHANGED_AT"), Str(r, "FROM_STATE"), Str(r, "TO_STATE"))).ToList();
    }

    private async Task<OeeLotCounts> LoadLotCountsAsync(
        string equipmentId, string fromStr, string toStr, CancellationToken ct)
    {
        var rows = await _dispatcher.QueryAsync(
            @"SELECT COALESCE(SUM(QTY), 0) AS TOTAL, COALESCE(SUM(DEFECT_QTY), 0) AS DEFECT
              FROM POM_LOT
              WHERE EQUIPMENT_ID = @eq AND TRACK_OUT_TIME >= @from AND TRACK_OUT_TIME < @to",
            Args(("eq", equipmentId), ("from", fromStr), ("to", toStr)), ct);
        var row = rows.Count > 0 ? rows[0] : null;
        return new OeeLotCounts(Dec(row, "TOTAL"), Dec(row, "DEFECT"));
    }

    private sealed record TargetRow(string EquipmentId, string PlantId, decimal IdealCycleTimeSec, decimal PlannedMinutes);

    // ── provider 무관 값 강제변환(Dapper가 반환하는 boxed 타입: long/double/decimal/string/DBNull) ──
    private static Dictionary<string, object> Args(params (string Key, object Value)[] ps)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in ps) d[k] = v;
        return d;
    }

    private static string Str(IReadOnlyDictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) && v is not null and not DBNull ? v.ToString()! : string.Empty;

    private static decimal Dec(IReadOnlyDictionary<string, object?>? r, string key)
    {
        if (r is null || !r.TryGetValue(key, out var v) || v is null or DBNull) return 0m;
        return v switch
        {
            decimal d => d,
            double db => (decimal)db,
            long l => l,
            int i => i,
            _ => decimal.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0m,
        };
    }

    private static bool Bool(IReadOnlyDictionary<string, object?> r, string key)
    {
        if (!r.TryGetValue(key, out var v) || v is null or DBNull) return false;
        return v switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            _ => v.ToString() is "1" or "true" or "True",
        };
    }

    private static DateTime Date(IReadOnlyDictionary<string, object?> r, string key)
    {
        if (!r.TryGetValue(key, out var v) || v is null or DBNull) return default;
        if (v is DateTime dt) return dt;
        return DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : default;
    }
}
