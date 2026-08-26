using System.Globalization;
using NexaOne.EST.Application.Oee;
using NexaOne.EST.Domain.Oee;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Infrastructure;

/// <summary>OEE 집계 구현(EST 모듈 소유) — 원자료(EST_EQUIPMENT_STATE_HISTORY 상태전이 · EST_EQUIPMENT_OUTPUT_EVENT 생산/불량 수량)를
/// 설정(EST_STATE_CATEGORY · EST_OEE_TARGET)과 결합해 <see cref="OeeCalculator"/>로 계산하고 EST_OEE_SUMMARY/
/// EST_OEE_LOSS 마트에 적재한다. DB 접근은 모듈 표준(<see cref="QueryRepository"/> 읽기 + <see cref="ServiceObjectProcessor"/>
/// 쓰기, provider-agnostic Dapper) — SQLite/MSSQL 공통 ANSI SQL. 워커 산출물은 OEE_ID='AGG_*'/LOSS_ID='AGL_*'로
/// 키잉해 데모 시드(OEE01~/LOSS01~)를 보존하고 delete+insert로 멱등이다. 외부 계획·생산 증거는
/// <see cref="IOeeEvidenceSource"/> seam으로만 읽으므로 EST는 다른 업무 모듈의 물리 스키마를 소유하지 않는다.</summary>
public sealed class OeeAggregationRepository : QueryRepository, IOeeAggregator
{
    private const string Ts = "yyyy-MM-dd HH:mm:ss";
    // 미분류 상태 폴백 — 계획시간엔 포함하되 가동/비가동 어느쪽도 아님(중립). 데이터 이상이 OEE를 왜곡하지 않게 한다.
    private static readonly OeeStateCategory Unknown = new("Unknown", IsProductive: false, IsDowntime: false, IsScheduled: true);

    private readonly ServiceObjectProcessor _processor;
    private readonly TaktAggregationRepository _taktAggregator;
    private readonly IOeeEvidenceSource _evidenceSource;

    public OeeAggregationRepository(EesDataSource dataSource, IOeeEvidenceSource evidenceSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _taktAggregator = new TaktAggregationRepository(dataSource);
        _evidenceSource = evidenceSource;
    }

    public async Task<int> AggregateDayAsync(DateTime date, CancellationToken ct = default)
    {
        var dayStart = date.Date;
        var definitions = await LoadTargetDefinitionsAsync(ct);
        var plan = await _evidenceSource.LoadPlanAsync(
            definitions.Select(static target => target.EquipmentId).ToArray(), dayStart, ct);
        var targets = BindTargets(definitions, plan);
        var plantDays = plan.PlantDays
            .GroupBy(static plantDay => plantDay.PlantId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var expectedScopes = new HashSet<GeneratedScope>();
        int total = 0;
        foreach (var plantTargets in targets.GroupBy(static target => target.PlantId, StringComparer.Ordinal))
        {
            if (!plantDays.TryGetValue(plantTargets.Key, out var plantDay)
                || plantDay.Windows.Count == 0)
                continue;
            var scopedTargets = plantTargets.ToList();
            foreach (var window in plantDay.Windows)
            {
                var shiftId = NormalizeShiftId(window.ShiftId);
                foreach (var target in scopedTargets)
                    expectedScopes.Add(new GeneratedScope(target.PlantId, target.EquipmentId, shiftId));
                total += await AggregateWindowInternalAsync(
                    window.StartUtc, window.EndUtc, shiftId, window.PlannedMinutes,
                    scopedTargets, dayStart, ct);
            }
        }
        // Reconciliation runs only after every current scope was recomputed successfully. A failed target therefore
        // keeps its previous rows, while deactivated targets, removed shifts and empty plans cannot leave stale marts.
        await ReconcileGeneratedRowsAsync(dayStart, dayStart.AddDays(1), expectedScopes, ct);
        return total;
    }

    public async Task<int> AggregateWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId = null, decimal plannedOverride = 0m,
        CancellationToken ct = default)
    {
        var definitions = await LoadTargetDefinitionsAsync(ct);
        var plan = await _evidenceSource.LoadPlanAsync(
            definitions.Select(static target => target.EquipmentId).ToArray(), localDay: null, ct);
        var targets = BindTargets(definitions, plan);
        var reportDate = windowStart.Date;
        var normalizedShiftId = NormalizeShiftId(shiftId);
        var written = await AggregateWindowInternalAsync(
            windowStart, windowEnd, normalizedShiftId, plannedOverride,
            targets, reportDate, ct);
        var expectedScopes = targets
            .Select(target => new GeneratedScope(target.PlantId, target.EquipmentId, normalizedShiftId))
            .ToHashSet();
        await ReconcileGeneratedRowsAsync(
            reportDate, reportDate.AddDays(1), expectedScopes, ct,
            new ReconciliationScope(normalizedShiftId));
        return written;
    }

    private async Task<int> AggregateWindowInternalAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId, decimal plannedOverride,
        IReadOnlyList<TargetRow> targets, DateTime reportDate, CancellationToken ct)
    {
        var fromStr = F(windowStart);
        var toStr = F(windowEnd);
        var suffix = string.IsNullOrEmpty(shiftId) ? "ALLDAY" : shiftId!;

        if (targets.Count == 0) return 0;
        var categories = await LoadCategoriesAsync(ct);

        int written = 0;
        foreach (var t in targets)
        {
            var transitions = await LoadTransitionsAsync(t.EquipmentId, fromStr, toStr, ct);
            var production = await _evidenceSource.LoadProductionAsync(
                t.PlantId, t.EquipmentId, windowStart, windowEnd, ct);
            var lots = await LoadOutputCountsAsync(t.EquipmentId, fromStr, toStr, production, ct);
            var result = OeeCalculator.Compute(
                windowStart, windowEnd, transitions, lots,
                new OeeTarget(t.IdealCycleTimeSec, t.PlannedMinutes), categories, Unknown, plannedOverride);

            var oeeId = $"AGG_{t.EquipmentId}_{reportDate:yyyyMMdd}_{suffix}";
            var lossPrefix = $"AGL_{t.EquipmentId}_{reportDate:yyyyMMdd}_{suffix}_";
            var statements = new List<(string Sql, object? Param)>
            {
                ("DELETE FROM EST_OEE_SUMMARY WHERE OEE_ID = @id", new { id = oeeId }),
                (@"DELETE FROM EST_OEE_LOSS
                   WHERE LOSS_ID LIKE 'AGL_%' AND EQUIPMENT_ID = @eq AND OEE_DATE = @date
                     AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)",
                    new { eq = t.EquipmentId, date = F(reportDate), shift = (object?)shiftId }),
                (InsertSummarySql, new
                {
                    id = oeeId, plant = t.PlantId, eq = t.EquipmentId, date = F(reportDate),
                    shift = (object?)shiftId,
                    planned = result.PlannedMinutes, downtime = result.DowntimeMinutes,
                    operating = result.OperatingMinutes, ict = t.IdealCycleTimeSec,
                    total = result.TotalCount, good = result.GoodCount, defect = result.DefectCount,
                    av = result.Availability, pf = result.Performance, ql = result.Quality, oee = result.Oee,
                })
            };

            int lossIdx = 0;
            foreach (var loss in result.Losses)
            {
                statements.Add((InsertLossSql, new
                {
                    id = lossPrefix + lossIdx++, plant = t.PlantId, eq = t.EquipmentId, date = F(reportDate),
                    shift = (object?)shiftId, cat = loss.Category, min = loss.Minutes,
                }));
            }

            await _processor.ExecuteManyAsync(ct, statements.ToArray());
            written++;
            await _taktAggregator.AggregateEquipmentWindowAsync(
                oeeId, t.PlantId, t.EquipmentId, reportDate, shiftId,
                windowStart, windowEnd, production.TrackOuts, ct);
        }
        return written;
    }

    private async Task<int> ReconcileGeneratedRowsAsync(
        DateTime fromDate,
        DateTime toDate,
        IReadOnlySet<GeneratedScope> expectedScopes,
        CancellationToken ct,
        ReconciliationScope? scopeFilter = null)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT 'TAKT' AS ROW_KIND, TAKT_SUMMARY_ID AS ROW_ID, PLANT_ID, EQUIPMENT_ID, SHIFT_ID
              FROM EST_TAKT_SUMMARY
              WHERE TAKT_SUMMARY_ID LIKE 'TKT_%' AND TAKT_DATE >= @from AND TAKT_DATE < @to
              UNION ALL
              SELECT 'LOSS' AS ROW_KIND, LOSS_ID AS ROW_ID, PLANT_ID, EQUIPMENT_ID, SHIFT_ID
              FROM EST_OEE_LOSS
              WHERE LOSS_ID LIKE 'AGL_%' AND OEE_DATE >= @from AND OEE_DATE < @to
              UNION ALL
              SELECT 'OEE' AS ROW_KIND, OEE_ID AS ROW_ID, PLANT_ID, EQUIPMENT_ID, SHIFT_ID
              FROM EST_OEE_SUMMARY
              WHERE OEE_ID LIKE 'AGG_%' AND OEE_DATE >= @from AND OEE_DATE < @to",
            new { from = F(fromDate), to = F(toDate) }, ct);
        var staleRows = rows
            .Select(Dict)
            .Select(row => new GeneratedRow(
                ParseGeneratedRowKind(Str(row, "ROW_KIND")),
                Str(row, "ROW_ID"),
                new GeneratedScope(
                    Str(row, "PLANT_ID"),
                    Str(row, "EQUIPMENT_ID"),
                    NormalizeShiftId(NullableStr(row, "SHIFT_ID")))))
            .Where(row => scopeFilter is null
                          || string.Equals(row.Scope.ShiftId, scopeFilter.ShiftId, StringComparison.Ordinal))
            .Where(row => !expectedScopes.Contains(row.Scope))
            .ToArray();
        if (staleRows.Length == 0) return 0;

        // Exact primary-key deletes keep the SQL ANSI/provider-neutral and avoid empty/large IN-list behavior.
        // All stale deletes share one transaction; dependents are ordered before OEE summaries for future FK safety.
        var statements = staleRows
            .OrderBy(static row => row.Kind)
            .Select(static row => row.Kind switch
            {
                GeneratedRowKind.Takt => (
                    "DELETE FROM EST_TAKT_SUMMARY WHERE TAKT_SUMMARY_ID = @id AND TAKT_SUMMARY_ID LIKE 'TKT_%'",
                    (object?)new { id = row.Id }),
                GeneratedRowKind.Loss => (
                    "DELETE FROM EST_OEE_LOSS WHERE LOSS_ID = @id AND LOSS_ID LIKE 'AGL_%'",
                    (object?)new { id = row.Id }),
                GeneratedRowKind.Oee => (
                    "DELETE FROM EST_OEE_SUMMARY WHERE OEE_ID = @id AND OEE_ID LIKE 'AGG_%'",
                    (object?)new { id = row.Id }),
                _ => throw new InvalidOperationException($"Unknown generated OEE row kind: {row.Kind}."),
            })
            .ToArray();
        return await _processor.ExecuteManyAsync(ct, statements);
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

    private async Task<List<TargetDefinition>> LoadTargetDefinitionsAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT EQUIPMENT_ID, IDEAL_CYCLE_TIME_SEC, PLANNED_MINUTES
              FROM EST_OEE_TARGET
              WHERE IS_ACTIVE = 1", null, ct);
        return rows.Select(Dict).Select(d => new TargetDefinition(
            Str(d, "EQUIPMENT_ID"), Dec(d, "IDEAL_CYCLE_TIME_SEC"), Dec(d, "PLANNED_MINUTES"))).ToList();
    }

    private static IReadOnlyList<TargetRow> BindTargets(
        IReadOnlyList<TargetDefinition> definitions,
        OeePlanSnapshotDto plan)
    {
        var scopes = plan.EquipmentScopes
            .Where(static scope => !string.IsNullOrWhiteSpace(scope.EquipmentId)
                                   && !string.IsNullOrWhiteSpace(scope.PlantId))
            .GroupBy(static scope => scope.EquipmentId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        return definitions
            .Where(definition => scopes.ContainsKey(definition.EquipmentId))
            .Select(definition => new TargetRow(
                definition.EquipmentId,
                scopes[definition.EquipmentId].PlantId,
                definition.IdealCycleTimeSec,
                definition.PlannedMinutes))
            .ToArray();
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
        var transitions = rows.Select(Dict).Select(d => new OeeStateTransition(
            Date(d, "CHANGED_AT"), Str(d, "FROM_STATE"), Str(d, "TO_STATE"))).ToList();

        var carryRows = await QueryAsync<dynamic>(
            @"SELECT CHANGED_AT, FROM_STATE, TO_STATE
              FROM EST_EQUIPMENT_STATE_HISTORY
              WHERE EQUIPMENT_ID = @eq AND CHANGED_AT < @from
              ORDER BY CHANGED_AT DESC",
            new { eq = equipmentId, from = fromStr }, ct);
        var carry = carryRows.Select(Dict).FirstOrDefault();
        if (carry is not null)
        {
            var state = Str(carry, "TO_STATE");
            transitions.Insert(0, new OeeStateTransition(ParseTimestamp(fromStr), state, state));
        }
        return transitions;
    }

    private async Task<OeeLotCounts> LoadOutputCountsAsync(
        string equipmentId,
        string fromStr,
        string toStr,
        OeeProductionWindowDto production,
        CancellationToken ct)
    {
        // Canonical non-LOT output and upstream LOT evidence can coexist in one window during rollout.
        // If upstream LOT evidence exists, it remains authoritative for the LOT scope while canonical
        // non-LOT events (carrier cleaning, tools, etc.) are added. Once upstream evidence is absent, canonical
        // LOT events take over too. The explicit scope prevents both omission and double counting.
        var outputRows = await QueryAsync<dynamic>(
            @"SELECT COUNT(*) AS EVENT_COUNT,
                     COALESCE(SUM(TOTAL_QTY), 0) AS TOTAL,
                     COALESCE(SUM(DEFECT_QTY), 0) AS DEFECT,
                     COALESCE(SUM(CASE
                         WHEN COALESCE(IS_LOT_OUTPUT,
                              CASE WHEN PROCESS_LOT_ID IS NULL THEN 0 ELSE 1 END) = 0
                         THEN TOTAL_QTY ELSE 0 END), 0) AS NON_LOT_TOTAL,
                     COALESCE(SUM(CASE
                         WHEN COALESCE(IS_LOT_OUTPUT,
                              CASE WHEN PROCESS_LOT_ID IS NULL THEN 0 ELSE 1 END) = 0
                         THEN DEFECT_QTY ELSE 0 END), 0) AS NON_LOT_DEFECT
              FROM EST_EQUIPMENT_OUTPUT_EVENT
              WHERE EQUIPMENT_ID = @eq AND OCCURRED_AT >= @from AND OCCURRED_AT < @to",
            new { eq = equipmentId, from = fromStr, to = toStr }, ct);
        var output = outputRows.Select(Dict).FirstOrDefault();

        if (production.LotEventCount > 0)
            return new OeeLotCounts(
                Dec(output, "NON_LOT_TOTAL") + production.LotTotalCount,
                Dec(output, "NON_LOT_DEFECT") + production.LotDefectCount);

        return new OeeLotCounts(Dec(output, "TOTAL"), Dec(output, "DEFECT"));
    }

    private static DateTime ParseTimestamp(string value)
        => DateTime.ParseExact(value, Ts, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string F(DateTime dt) => dt.ToString(Ts, CultureInfo.InvariantCulture);

    private static string? NormalizeShiftId(string? shiftId)
        => string.IsNullOrWhiteSpace(shiftId) ? null : shiftId;

    private static GeneratedRowKind ParseGeneratedRowKind(string value) => value switch
    {
        "TAKT" => GeneratedRowKind.Takt,
        "LOSS" => GeneratedRowKind.Loss,
        "OEE" => GeneratedRowKind.Oee,
        _ => throw new InvalidOperationException($"Unknown generated OEE row kind: {value}."),
    };

    private sealed record TargetDefinition(
        string EquipmentId, decimal IdealCycleTimeSec, decimal PlannedMinutes);
    private sealed record TargetRow(
        string EquipmentId, string PlantId, decimal IdealCycleTimeSec, decimal PlannedMinutes);
    private sealed record GeneratedScope(string PlantId, string EquipmentId, string? ShiftId);
    private sealed record ReconciliationScope(string? ShiftId);
    private sealed record GeneratedRow(GeneratedRowKind Kind, string Id, GeneratedScope Scope);
    private enum GeneratedRowKind { Takt, Loss, Oee }

    // ── provider 무관 값 강제변환(Dapper dynamic 행 = IDictionary<string,object>; boxed long/double/decimal/string/DBNull) ──
    private static IDictionary<string, object> Dict(dynamic row) => (IDictionary<string, object>)row;

    private static string Str(IDictionary<string, object>? r, string key)
        => r is not null && r.TryGetValue(key, out var v) && v is not null and not DBNull ? v.ToString()! : string.Empty;

    private static string? NullableStr(IDictionary<string, object> r, string key)
        => r.TryGetValue(key, out var v) && v is not null and not DBNull ? v.ToString() : null;

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
