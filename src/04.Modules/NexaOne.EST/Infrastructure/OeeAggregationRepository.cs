using System.Globalization;
using NexaOne.EST.Application.Oee;
using NexaOne.EST.Domain.Oee;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EST.Infrastructure;

/// <summary>OEE 집계 구현(EST 모듈 소유) — 원자료(EST_EQUIPMENT_STATE_HISTORY 상태전이 · POM_LOT 생산/불량 수량)를
/// 설정(EST_STATE_CATEGORY · EST_OEE_TARGET)과 결합해 <see cref="OeeCalculator"/>로 계산하고 EST_OEE_SUMMARY/
/// EST_OEE_LOSS 마트에 적재한다. DB 접근은 모듈 표준(<see cref="QueryRepository"/> 읽기 + <see cref="ServiceObjectProcessor"/>
/// 쓰기, provider-agnostic Dapper) — SQLite/MSSQL 공통 ANSI SQL. 워커 산출물은 OEE_ID='AGG_*'/LOSS_ID='AGL_*'로
/// 키잉해 데모 시드(OEE01~/LOSS01~)를 보존하고 delete+insert로 멱등이다. 작업조 인식은 근무달력(MDM_WORK_CALENDAR)→
/// 활성 작업조(MDM_SHIFT)로 윈도를 해석하고 계획시간을 작업조 길이로 확정한다.
/// 교차모듈 데이터(POM_LOT·MDM_SHIFT/CALENDAR/EQUIPMENT)는 C# 타입 결합 없이 순수 SQL로만 읽는다(ADR-001).</summary>
public sealed class OeeAggregationRepository : QueryRepository, IOeeAggregator
{
    private const string Ts = "yyyy-MM-dd HH:mm:ss";
    // 미분류 상태 폴백 — 계획시간엔 포함하되 가동/비가동 어느쪽도 아님(중립). 데이터 이상이 OEE를 왜곡하지 않게 한다.
    private static readonly OeeStateCategory Unknown = new("Unknown", IsProductive: false, IsDowntime: false, IsScheduled: true);

    private readonly ServiceObjectProcessor _processor;

    public OeeAggregationRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<int> AggregateDayAsync(DateTime date, CancellationToken ct = default)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);

        // 일자 범위의 기존 워커 산출물을 한 번에 제거(멱등) — 작업조/일자 모드 전환 시 잔여행 방지.
        await DeleteWorkerRowsAsync(F(dayStart), F(dayEnd), ct);

        var windows = await ResolveShiftWindowsAsync(dayStart, ct);
        if (windows.Count == 0)
            return await AggregateWindowInternalAsync(dayStart, dayEnd, shiftId: null, plannedOverride: 0m, deleteFirst: false, ct);

        int total = 0;
        foreach (var w in windows)
            total += await AggregateWindowInternalAsync(w.Start, w.End, w.ShiftId, w.PlannedMinutes, deleteFirst: false, ct);
        return total;
    }

    public Task<int> AggregateWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId = null, decimal plannedOverride = 0m,
        CancellationToken ct = default)
        => AggregateWindowInternalAsync(windowStart, windowEnd, shiftId, plannedOverride, deleteFirst: true, ct);

    private async Task<int> AggregateWindowInternalAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId, decimal plannedOverride, bool deleteFirst, CancellationToken ct)
    {
        var fromStr = F(windowStart);
        var toStr = F(windowEnd);
        var suffix = string.IsNullOrEmpty(shiftId) ? "ALLDAY" : shiftId!;

        var categories = await LoadCategoriesAsync(ct);
        var targets = await LoadTargetsAsync(ct);
        if (targets.Count == 0) return 0;

        if (deleteFirst) await DeleteWorkerRowsAsync(fromStr, toStr, ct);

        int written = 0;
        foreach (var t in targets)
        {
            var transitions = await LoadTransitionsAsync(t.EquipmentId, fromStr, toStr, ct);
            var lots = await LoadLotCountsAsync(t.EquipmentId, fromStr, toStr, ct);
            var result = OeeCalculator.Compute(
                windowStart, windowEnd, transitions, lots,
                new OeeTarget(t.IdealCycleTimeSec, t.PlannedMinutes), categories, Unknown, plannedOverride);

            var oeeId = $"AGG_{t.EquipmentId}_{windowStart:yyyyMMdd}_{suffix}";
            await _processor.ExecuteAsync(InsertSummarySql, new
            {
                id = oeeId, plant = t.PlantId, eq = t.EquipmentId, date = fromStr,
                shift = (object?)shiftId,
                planned = result.PlannedMinutes, downtime = result.DowntimeMinutes,
                operating = result.OperatingMinutes, ict = t.IdealCycleTimeSec,
                total = result.TotalCount, good = result.GoodCount, defect = result.DefectCount,
                av = result.Availability, pf = result.Performance, ql = result.Quality, oee = result.Oee,
            }, ct);
            written++;

            int lossIdx = 0;
            foreach (var loss in result.Losses)
            {
                var lossId = $"AGL_{t.EquipmentId}_{windowStart:yyyyMMdd}_{suffix}_{lossIdx++}";
                await _processor.ExecuteAsync(InsertLossSql, new
                {
                    id = lossId, plant = t.PlantId, eq = t.EquipmentId, date = fromStr,
                    shift = (object?)shiftId, cat = loss.Category, min = loss.Minutes,
                }, ct);
            }
        }
        return written;
    }

    private async Task DeleteWorkerRowsAsync(string fromStr, string toStr, CancellationToken ct)
    {
        await _processor.ExecuteAsync(
            "DELETE FROM EST_OEE_SUMMARY WHERE OEE_ID LIKE 'AGG_%' AND OEE_DATE >= @from AND OEE_DATE < @to",
            new { from = fromStr, to = toStr }, ct);
        await _processor.ExecuteAsync(
            "DELETE FROM EST_OEE_LOSS WHERE LOSS_ID LIKE 'AGL_%' AND OEE_DATE >= @from AND OEE_DATE < @to",
            new { from = fromStr, to = toStr }, ct);
    }

    private const string InsertSummarySql = @"
        INSERT INTO EST_OEE_SUMMARY
        (OEE_ID, PLANT_ID, EQUIPMENT_ID, OEE_DATE, SHIFT_ID,
         PLANNED_MINUTES, DOWNTIME_MINUTES, OPERATING_MINUTES, IDEAL_CYCLE_TIME_SEC,
         TOTAL_COUNT, GOOD_COUNT, DEFECT_COUNT, AVAILABILITY, PERFORMANCE, QUALITY, OEE)
        VALUES
        (@id, @plant, @eq, @date, @shift,
         @planned, @downtime, @operating, @ict,
         @total, @good, @defect, @av, @pf, @ql, @oee)";

    private const string InsertLossSql = @"
        INSERT INTO EST_OEE_LOSS
        (LOSS_ID, PLANT_ID, EQUIPMENT_ID, OEE_DATE, SHIFT_ID, LOSS_CATEGORY, LOSS_MINUTES, OCCURRED_AT)
        VALUES (@id, @plant, @eq, @date, @shift, @cat, @min, @date)";

    private async Task<IReadOnlyDictionary<string, OeeStateCategory>> LoadCategoriesAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT STATE_ID, CATEGORY, IS_PRODUCTIVE, IS_DOWNTIME, IS_SCHEDULED FROM EST_STATE_CATEGORY", null, ct);
        var map = new Dictionary<string, OeeStateCategory>(StringComparer.Ordinal);
        foreach (var d in rows.Select(Dict))
            map[Str(d, "STATE_ID")] = new OeeStateCategory(
                Str(d, "CATEGORY"), Bool(d, "IS_PRODUCTIVE"), Bool(d, "IS_DOWNTIME"), Bool(d, "IS_SCHEDULED"));
        return map;
    }

    private async Task<List<TargetRow>> LoadTargetsAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT t.EQUIPMENT_ID, e.PLANT_ID, t.IDEAL_CYCLE_TIME_SEC, t.PLANNED_MINUTES
              FROM EST_OEE_TARGET t
              JOIN MDM_EQUIPMENT e ON e.EQUIPMENT_ID = t.EQUIPMENT_ID
              WHERE t.IS_ACTIVE = 1", null, ct);
        return rows.Select(Dict).Select(d => new TargetRow(
            Str(d, "EQUIPMENT_ID"), Str(d, "PLANT_ID"),
            Dec(d, "IDEAL_CYCLE_TIME_SEC"), Dec(d, "PLANNED_MINUTES"))).ToList();
    }

    private async Task<IReadOnlyList<OeeStateTransition>> LoadTransitionsAsync(
        string equipmentId, string fromStr, string toStr, CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT CHANGED_AT, FROM_STATE, TO_STATE
              FROM EST_EQUIPMENT_STATE_HISTORY
              WHERE EQUIPMENT_ID = @eq AND CHANGED_AT >= @from AND CHANGED_AT < @to
              ORDER BY CHANGED_AT",
            new { eq = equipmentId, from = fromStr, to = toStr }, ct);
        return rows.Select(Dict).Select(d => new OeeStateTransition(
            Date(d, "CHANGED_AT"), Str(d, "FROM_STATE"), Str(d, "TO_STATE"))).ToList();
    }

    private async Task<OeeLotCounts> LoadLotCountsAsync(
        string equipmentId, string fromStr, string toStr, CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT COALESCE(SUM(QTY), 0) AS TOTAL, COALESCE(SUM(DEFECT_QTY), 0) AS DEFECT
              FROM POM_LOT
              WHERE EQUIPMENT_ID = @eq AND TRACK_OUT_TIME >= @from AND TRACK_OUT_TIME < @to",
            new { eq = equipmentId, from = fromStr, to = toStr }, ct);
        var d = rows.Select(Dict).FirstOrDefault();
        return new OeeLotCounts(Dec(d, "TOTAL"), Dec(d, "DEFECT"));
    }

    /// <summary>그 날의 작업조 윈도를 해석한다. 근무달력(MDM_WORK_CALENDAR, Holiday 제외)에 항목이 있으면 그 작업조만,
    /// 없으면 활성 작업조(MDM_SHIFT) 전체를 매일 적용한다. 야간(종료≤시작)은 익일로 넘긴다. 파싱 불가 시간은 건너뛴다.</summary>
    private async Task<List<ShiftWindow>> ResolveShiftWindowsAsync(DateTime day, CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT DISTINCT s.SHIFT_ID, s.START_TIME, s.END_TIME
              FROM MDM_WORK_CALENDAR c
              JOIN MDM_SHIFT s ON s.SHIFT_ID = c.SHIFT_ID
              WHERE c.CALENDAR_DATE >= @day AND c.CALENDAR_DATE < @next
                AND s.IS_ACTIVE = 1 AND (c.DAY_TYPE IS NULL OR c.DAY_TYPE <> 'Holiday')",
            new { day = F(day), next = F(day.AddDays(1)) }, ct);
        var list = rows.Select(Dict).ToList();
        if (list.Count == 0)
            list = (await QueryAsync<dynamic>(
                "SELECT SHIFT_ID, START_TIME, END_TIME FROM MDM_SHIFT WHERE IS_ACTIVE = 1", null, ct))
                .Select(Dict).ToList();

        var windows = new List<ShiftWindow>();
        foreach (var d in list)
        {
            if (!TryTime(Str(d, "START_TIME"), out var start) || !TryTime(Str(d, "END_TIME"), out var end)) continue;
            var startDt = day + start;
            var endDt = day + end;
            if (endDt <= startDt) endDt = endDt.AddDays(1); // 야간 교대
            var planned = (decimal)(endDt - startDt).TotalMinutes;
            if (planned <= 0m) continue;
            windows.Add(new ShiftWindow(Str(d, "SHIFT_ID"), startDt, endDt, planned));
        }
        return windows;
    }

    private static bool TryTime(string hhmm, out TimeSpan ts)
        => TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1);

    private static string F(DateTime dt) => dt.ToString(Ts, CultureInfo.InvariantCulture);

    private sealed record TargetRow(string EquipmentId, string PlantId, decimal IdealCycleTimeSec, decimal PlannedMinutes);
    private sealed record ShiftWindow(string ShiftId, DateTime Start, DateTime End, decimal PlannedMinutes);

    // ── provider 무관 값 강제변환(Dapper dynamic 행 = IDictionary<string,object>; boxed long/double/decimal/string/DBNull) ──
    private static IDictionary<string, object> Dict(dynamic row) => (IDictionary<string, object>)row;

    private static string Str(IDictionary<string, object>? r, string key)
        => r is not null && r.TryGetValue(key, out var v) && v is not null and not DBNull ? v.ToString()! : string.Empty;

    private static decimal Dec(IDictionary<string, object>? r, string key)
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

    private static bool Bool(IDictionary<string, object> r, string key)
    {
        if (!r.TryGetValue(key, out var v) || v is null or DBNull) return false;
        return v switch { bool b => b, long l => l != 0, int i => i != 0, _ => v.ToString() is "1" or "true" or "True" };
    }

    private static DateTime Date(IDictionary<string, object> r, string key)
    {
        if (!r.TryGetValue(key, out var v) || v is null or DBNull) return default;
        if (v is DateTime dt) return dt;
        return DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : default;
    }
}
