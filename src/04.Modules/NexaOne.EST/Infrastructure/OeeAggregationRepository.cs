using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    public Task<int> AggregateDayAsync(DateTime date, CancellationToken ct = default)
        => AggregateDayCoreAsync(
            date, plantFilter: null, AggregationKind.AutomaticDay, aggregationRunId: null, ct);

    public async Task<int> AggregateRecentLocalDaysAsync(
        DateTime utcNow,
        int lookbackDays,
        CancellationToken ct = default)
    {
        if (lookbackDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(lookbackDays), "Lookback days must be positive.");

        var definitions = await LoadTargetDefinitionsAsync(ct);
        var equipmentIds = definitions.Select(static target => target.EquipmentId).ToArray();
        var clocks = await _evidenceSource.LoadPlantLocalDatesAsync(equipmentIds, Utc(utcNow), ct);
        if (clocks.Count == 0)
        {
            var fallback = Utc(utcNow).Date;
            var fallbackTotal = 0;
            for (var offset = lookbackDays - 1; offset >= 0; offset--)
                fallbackTotal += await AggregateDayCoreAsync(
                    fallback.AddDays(-offset), null, AggregationKind.AutomaticDay, null, ct);
            return fallbackTotal;
        }

        var requestedDays = clocks
            .Where(static clock => !string.IsNullOrWhiteSpace(clock.PlantId))
            .SelectMany(clock => Enumerable.Range(0, lookbackDays)
                .Select(offset => new { clock.PlantId, Day = clock.LocalDate.Date.AddDays(-offset) }))
            .GroupBy(static request => request.Day)
            .OrderBy(static group => group.Key)
            .ToArray();
        var total = 0;
        foreach (var requestedDay in requestedDays)
        {
            var plants = requestedDay
                .Select(static request => request.PlantId)
                .ToHashSet(StringComparer.Ordinal);
            total += await AggregateDayCoreAsync(
                requestedDay.Key, plants, AggregationKind.AutomaticDay, null, ct);
        }
        return total;
    }

    public async Task<int> AggregateDayManuallyAsync(
        DateTime localDate,
        string actorId,
        CancellationToken ct = default)
    {
        var actor = RequiredActor(actorId);
        var day = localDate.Date;
        var run = NewRun("ManualDay", ScopeKey($"DAY|{day:yyyy-MM-dd}"), actor,
            day, null, null, null, 0m);
        await StartRunAsync(run, ct);
        try
        {
            var batch = await StageDayAsync(
                day, plantFilter: null, AggregationKind.ManualDay, run.RunId, ct);
            batch.Statements.Add(CompleteRunStatement(run.RunId, batch.Written));
            await PublishAsync(batch, ct);
            return batch.Written;
        }
        catch (Exception ex)
        {
            await FailRunAsync(run.RunId, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<int> AggregateDayCoreAsync(
        DateTime date,
        IReadOnlySet<string>? plantFilter,
        AggregationKind aggregationKind,
        string? aggregationRunId,
        CancellationToken ct)
    {
        var batch = await StageDayAsync(
            date, plantFilter, aggregationKind, aggregationRunId, ct);
        await PublishAsync(batch, ct);
        return batch.Written;
    }

    private async Task<AggregationBatch> StageDayAsync(
        DateTime date,
        IReadOnlySet<string>? plantFilter,
        AggregationKind aggregationKind,
        string? aggregationRunId,
        CancellationToken ct)
    {
        var dayStart = date.Date;
        var definitions = await LoadTargetDefinitionsAsync(ct);
        var plan = await _evidenceSource.LoadPlanAsync(
            definitions.Select(static target => target.EquipmentId).ToArray(), dayStart, ct);
        var targets = BindTargets(definitions, plan)
            .Where(target => plantFilter is null || plantFilter.Contains(target.PlantId))
            .ToArray();
        var plantDays = plan.PlantDays
            .GroupBy(static plantDay => plantDay.PlantId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var expectedScopes = new HashSet<GeneratedScope>();
        var batch = new AggregationBatch();
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
                var windowBatch = await StageWindowAsync(
                    window.StartUtc, window.EndUtc, shiftId, window.PlannedMinutes,
                    scopedTargets, dayStart, aggregationKind, aggregationRunId, ct);
                batch.Append(windowBatch);
            }
        }
        // Reconciliation runs only after every current scope was recomputed successfully. A failed target therefore
        // keeps its previous rows, while deactivated targets, removed shifts and empty plans cannot leave stale marts.
        batch.Statements.AddRange(await BuildReconciliationStatementsAsync(
            dayStart, dayStart.AddDays(1), expectedScopes, ct,
            plantFilter: plantFilter));
        return batch;
    }

    public async Task<int> AggregateWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId = null, decimal plannedOverride = 0m,
        CancellationToken ct = default)
    {
        ValidateWindow(windowStart, windowEnd);
        windowStart = Utc(windowStart);
        windowEnd = Utc(windowEnd);
        var definitions = await LoadTargetDefinitionsAsync(ct);
        var plan = await _evidenceSource.LoadPlanAsync(
            definitions.Select(static target => target.EquipmentId).ToArray(), localDay: null, ct);
        var targets = BindTargets(definitions, plan);
        var reportDate = windowStart.Date;
        var normalizedShiftId = NormalizeShiftId(shiftId);
        var batch = await StageWindowAsync(
            windowStart, windowEnd, normalizedShiftId, plannedOverride,
            targets, reportDate, AggregationKind.RebuildWindow, null, ct);
        var expectedScopes = targets
            .Select(target => new GeneratedScope(target.PlantId, target.EquipmentId, normalizedShiftId))
            .ToHashSet();
        batch.Statements.AddRange(await BuildReconciliationStatementsAsync(
            reportDate, reportDate.AddDays(1), expectedScopes, ct,
            new ReconciliationScope(normalizedShiftId)));
        await PublishAsync(batch, ct);
        return batch.Written;
    }

    public async Task<int> AggregateWindowManuallyAsync(
        DateTime windowStart,
        DateTime windowEnd,
        string? shiftId,
        decimal plannedOverride,
        string actorId,
        CancellationToken ct = default)
    {
        ValidateWindow(windowStart, windowEnd);
        windowStart = Utc(windowStart);
        windowEnd = Utc(windowEnd);
        if (plannedOverride < 0m)
            throw new ArgumentOutOfRangeException(nameof(plannedOverride), "Planned minutes cannot be negative.");
        var actor = RequiredActor(actorId);
        var normalizedShiftId = NormalizeShiftId(shiftId);
        var scopeKey = ScopeKey(
            $"WINDOW|{P(windowStart)}|{P(windowEnd)}|{normalizedShiftId ?? "ALLDAY"}|{plannedOverride}");
        var run = NewRun("ManualWindow", scopeKey, actor, null,
            windowStart, windowEnd, normalizedShiftId, plannedOverride);
        await StartRunAsync(run, ct);

        try
        {
            var definitions = await LoadTargetDefinitionsAsync(ct);
            var plan = await _evidenceSource.LoadPlanAsync(
                definitions.Select(static target => target.EquipmentId).ToArray(), localDay: null, ct);
            var targets = BindTargets(definitions, plan);
            var batch = await StageWindowAsync(
                windowStart, windowEnd, normalizedShiftId, plannedOverride,
                targets, windowStart.Date, AggregationKind.ManualWindow, run.RunId, ct);
            batch.Statements.Add(CompleteRunStatement(run.RunId, batch.Written));
            await PublishAsync(batch, ct);
            return batch.Written;
        }
        catch (Exception ex)
        {
            await FailRunAsync(run.RunId, ex, CancellationToken.None);
            throw;
        }
    }

    private async Task<AggregationBatch> StageWindowAsync(
        DateTime windowStart, DateTime windowEnd, string? shiftId, decimal plannedOverride,
        IReadOnlyList<TargetRow> targets, DateTime reportDate, AggregationKind aggregationKind,
        string? aggregationRunId, CancellationToken ct)
    {
        // Existing SQLite evidence is stored with both second and fractional-second text shapes.
        // Querying with a forced fractional suffix makes an exact second at the lower boundary sort
        // before the parameter (and an exact upper boundary sort inside the window). Keep predicate
        // parameters at the repository's established second precision, while provenance/scope retain
        // the caller's full precision so distinct manual windows cannot overwrite each other.
        var queryFrom = F(windowStart);
        var queryTo = F(windowEnd);
        var fromStr = P(windowStart);
        var toStr = P(windowEnd);
        var suffix = string.IsNullOrEmpty(shiftId) ? "ALLDAY" : shiftId!;
        var kind = aggregationKind.ToString();

        if (targets.Count == 0) return new AggregationBatch();
        var categories = await LoadCategoriesAsync(ct);
        var equipmentIds = targets
            .Select(static target => target.EquipmentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var transitionsByEquipment = await LoadTransitionsAsync(
            equipmentIds, queryFrom, queryTo, ct);
        var canonicalByScope = await LoadCanonicalOutputsAsync(
            equipmentIds, queryFrom, queryTo, ct);
        var productionSnapshots = await Task.WhenAll(targets.Select(async target =>
            new ProductionSnapshot(
                new EvidenceScope(target.PlantId, target.EquipmentId),
                await _evidenceSource.LoadProductionAsync(
                    target.PlantId, target.EquipmentId, windowStart, windowEnd, ct))));
        var productionByScope = productionSnapshots.ToDictionary(
            static snapshot => snapshot.Scope,
            static snapshot => snapshot.Production);

        var batch = new AggregationBatch();
        var taktInputs = new List<TaktAggregationRepository.TaktWindowInput>();
        foreach (var t in targets)
        {
            var evidenceScope = new EvidenceScope(t.PlantId, t.EquipmentId);
            var scopeHash = ScopeKey(
                $"{t.PlantId}|{t.EquipmentId}|{fromStr}|{toStr}|{shiftId ?? "ALLDAY"}");
            var isManualWindow = aggregationKind == AggregationKind.ManualWindow;
            var oeeId = isManualWindow
                ? "MAN_" + scopeHash[..32]
                : $"AGG_{t.EquipmentId}_{reportDate:yyyyMMdd}_{suffix}";
            var lossPrefix = isManualWindow
                ? "MNL_" + scopeHash[..32] + "_"
                : $"AGL_{t.EquipmentId}_{reportDate:yyyyMMdd}_{suffix}_";
            var transitions = transitionsByEquipment.TryGetValue(t.EquipmentId, out var loadedTransitions)
                ? loadedTransitions
                : [];
            var production = productionByScope[evidenceScope];
            var canonical = canonicalByScope.TryGetValue(evidenceScope, out var loadedCanonical)
                ? loadedCanonical
                : [];
            OeeLotCounts lots;
            try
            {
                lots = BuildOutputCounts(
                    t.PlantId, t.EquipmentId, queryFrom, queryTo, canonical, production);
            }
            catch (OeeOutputUnitMismatchException)
            {
                await InvalidateGeneratedScopeAsync(
                    t, reportDate, shiftId, fromStr, toStr, isManualWindow, oeeId, ct);
                throw;
            }
            var result = OeeCalculator.Compute(
                windowStart, windowEnd, transitions, lots,
                new OeeTarget(t.IdealCycleTimeSec, t.PlannedMinutes), categories, Unknown, plannedOverride);

            batch.Statements.Add((
                "DELETE FROM EST_OEE_SUMMARY WHERE OEE_ID = @id", new { id = oeeId }));
            batch.Statements.Add(isManualWindow
                ? (@"DELETE FROM EST_OEE_LOSS
                     WHERE LOSS_ID LIKE 'MNL_%' AND EQUIPMENT_ID = @eq
                       AND WINDOW_START_UTC = @from AND WINDOW_END_UTC = @to
                       AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)",
                    new { eq = t.EquipmentId, from = fromStr, to = toStr, shift = (object?)shiftId })
                : (@"DELETE FROM EST_OEE_LOSS
                     WHERE LOSS_ID LIKE 'AGL_%' AND EQUIPMENT_ID = @eq AND OEE_DATE = @date
                       AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)",
                    new { eq = t.EquipmentId, date = F(reportDate), shift = (object?)shiftId }));
            batch.Statements.Add((InsertSummarySql, new
            {
                id = oeeId, plant = t.PlantId, eq = t.EquipmentId, date = F(reportDate),
                shift = (object?)shiftId,
                planned = result.PlannedMinutes, downtime = result.DowntimeMinutes,
                operating = result.OperatingMinutes, ict = t.IdealCycleTimeSec,
                total = result.TotalCount, good = result.GoodCount, defect = result.DefectCount,
                av = result.Availability, pf = result.Performance, ql = result.Quality, oee = result.Oee,
                kind, from = fromStr, to = toStr, run = (object?)aggregationRunId,
            }));

            int lossIdx = 0;
            foreach (var loss in result.Losses)
            {
                batch.Statements.Add((InsertLossSql, new
                {
                    id = lossPrefix + lossIdx++.ToString("D4", CultureInfo.InvariantCulture),
                    plant = t.PlantId, eq = t.EquipmentId, date = F(reportDate),
                    shift = (object?)shiftId, cat = loss.Category, min = loss.Minutes,
                    occurred = P(loss.OccurredAt), ended = P(loss.EndedAt),
                    kind, from = fromStr, to = toStr, run = (object?)aggregationRunId,
                }));
            }

            batch.Written++;
            if (!isManualWindow)
            {
                taktInputs.Add(new TaktAggregationRepository.TaktWindowInput(
                    t.PlantId, t.EquipmentId, reportDate, shiftId,
                    windowStart, windowEnd, result.Availability, production.TrackOuts));
            }
        }

        var taktBatch = await _taktAggregator.StageEquipmentWindowsAsync(taktInputs, ct);
        batch.Statements.AddRange(taktBatch.Statements);
        return batch;
    }

    private async Task InvalidateGeneratedScopeAsync(
        TargetRow target,
        DateTime reportDate,
        string? shiftId,
        string from,
        string to,
        bool isManual,
        string oeeId,
        CancellationToken ct)
    {
        var statements = new List<(string Sql, object? Param)>
        {
            ("DELETE FROM EST_OEE_SUMMARY WHERE OEE_ID = @id", new { id = oeeId }),
            isManual
                ? (@"DELETE FROM EST_OEE_LOSS
                     WHERE LOSS_ID LIKE 'MNL_%' AND EQUIPMENT_ID = @eq
                       AND WINDOW_START_UTC = @from AND WINDOW_END_UTC = @to
                       AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)",
                    new { eq = target.EquipmentId, from, to, shift = (object?)shiftId })
                : (@"DELETE FROM EST_OEE_LOSS
                     WHERE LOSS_ID LIKE 'AGL_%' AND EQUIPMENT_ID = @eq AND OEE_DATE = @date
                       AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)",
                    new { eq = target.EquipmentId, date = F(reportDate), shift = (object?)shiftId }),
        };
        if (!isManual)
        {
            statements.Add((@"DELETE FROM EST_TAKT_SUMMARY
                              WHERE TAKT_SUMMARY_ID LIKE 'TKT_%'
                                AND PLANT_ID = @plant AND EQUIPMENT_ID = @eq AND TAKT_DATE = @date
                                AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)",
                new
                {
                    plant = target.PlantId,
                    eq = target.EquipmentId,
                    date = F(reportDate),
                    shift = (object?)shiftId,
                }));
        }
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
    }

    private async Task<IReadOnlyList<(string Sql, object? Param)>> BuildReconciliationStatementsAsync(
        DateTime fromDate,
        DateTime toDate,
        IReadOnlySet<GeneratedScope> expectedScopes,
        CancellationToken ct,
        ReconciliationScope? scopeFilter = null,
        IReadOnlySet<string>? plantFilter = null)
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
            .Where(row => plantFilter is null || plantFilter.Contains(row.Scope.PlantId))
            .Where(row => scopeFilter is null
                          || string.Equals(row.Scope.ShiftId, scopeFilter.ShiftId, StringComparison.Ordinal))
            .Where(row => !expectedScopes.Contains(row.Scope))
            .ToArray();
        if (staleRows.Length == 0) return [];

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
        return statements;
    }

    private const string InsertSummarySql = @"
        INSERT INTO EST_OEE_SUMMARY
        (OEE_ID, PLANT_ID, EQUIPMENT_ID, OEE_DATE, SHIFT_ID,
         PLANNED_MINUTES, DOWNTIME_MINUTES, OPERATING_MINUTES, IDEAL_CYCLE_TIME_SEC,
         TOTAL_COUNT, GOOD_COUNT, DEFECT_COUNT, AVAILABILITY, PERFORMANCE, QUALITY, OEE,
         AGGREGATION_KIND, WINDOW_START_UTC, WINDOW_END_UTC, AGGREGATION_RUN_ID)
        VALUES
        (@id, @plant, @eq, @date, @shift,
         @planned, @downtime, @operating, @ict,
         @total, @good, @defect, @av, @pf, @ql, @oee,
         @kind, @from, @to, @run)";

    private const string InsertLossSql = @"
        INSERT INTO EST_OEE_LOSS
        (LOSS_ID, PLANT_ID, EQUIPMENT_ID, OEE_DATE, SHIFT_ID, LOSS_CATEGORY, LOSS_MINUTES,
         OCCURRED_AT, ENDED_AT, AGGREGATION_KIND, WINDOW_START_UTC, WINDOW_END_UTC, AGGREGATION_RUN_ID)
        VALUES (@id, @plant, @eq, @date, @shift, @cat, @min,
                @occurred, @ended, @kind, @from, @to, @run)";

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

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<OeeStateTransition>>> LoadTransitionsAsync(
        IReadOnlyList<string> equipmentIds,
        string fromStr,
        string toStr,
        CancellationToken ct)
    {
        if (equipmentIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<OeeStateTransition>>(StringComparer.Ordinal);

        // The carry subquery ranks inside the database and emits exactly one pre-window state per equipment.
        // Together with the composite history index this avoids transferring an equipment's full history and
        // collapses the former two queries per equipment into one batched query for the whole window.
        var rows = await QueryAsync<TransitionRow>(
            @"SELECT EQUIPMENT_ID AS EquipmentId, HIST_ID AS HistoryId,
                     CHANGED_AT AS ChangedAt, SET_STATE AS SetState, 0 AS IsCarry
              FROM EST_EQUIPMENT_STATE_HISTORY
              WHERE EQUIPMENT_ID IN @equipmentIds AND CHANGED_AT >= @from AND CHANGED_AT < @to
              UNION ALL
              SELECT EQUIPMENT_ID AS EquipmentId, HIST_ID AS HistoryId,
                     CHANGED_AT AS ChangedAt, SET_STATE AS SetState, 1 AS IsCarry
              FROM (
                  SELECT EQUIPMENT_ID, HIST_ID, CHANGED_AT, SET_STATE,
                         ROW_NUMBER() OVER (
                             PARTITION BY EQUIPMENT_ID
                             ORDER BY CHANGED_AT DESC, HIST_ID DESC) AS RN
                  FROM EST_EQUIPMENT_STATE_HISTORY
                  WHERE EQUIPMENT_ID IN @equipmentIds AND CHANGED_AT < @from
              ) CARRY
              WHERE RN = 1
              ORDER BY EquipmentId, ChangedAt, IsCarry DESC, HistoryId",
            new { equipmentIds, from = fromStr, to = toStr }, ct);

        var result = equipmentIds
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                static equipmentId => equipmentId,
                static _ => (IReadOnlyList<OeeStateTransition>)Array.Empty<OeeStateTransition>(),
                StringComparer.Ordinal);
        foreach (var equipmentRows in rows.GroupBy(static row => row.EquipmentId, StringComparer.Ordinal))
        {
            var transitions = new List<OeeStateTransition>();
            var previousSetState = string.Empty;
            var carry = equipmentRows.FirstOrDefault(static row => row.IsCarry);
            if (carry is not null)
            {
                previousSetState = carry.SetState;
                transitions.Add(new OeeStateTransition(
                    carry.ChangedAt, string.Empty, previousSetState));
            }
            foreach (var row in equipmentRows
                         .Where(static row => !row.IsCarry)
                         .OrderBy(static row => row.ChangedAt)
                         .ThenBy(static row => row.HistoryId, StringComparer.Ordinal))
            {
                transitions.Add(new OeeStateTransition(
                    row.ChangedAt, previousSetState, row.SetState));
                previousSetState = row.SetState;
            }
            result[equipmentRows.Key] = transitions;
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<EvidenceScope, IReadOnlyList<CanonicalOutputRow>>> LoadCanonicalOutputsAsync(
        IReadOnlyList<string> equipmentIds,
        string fromStr,
        string toStr,
        CancellationToken ct)
    {
        if (equipmentIds.Count == 0)
            return new Dictionary<EvidenceScope, IReadOnlyList<CanonicalOutputRow>>();

        var canonical = await QueryAsync<CanonicalOutputRow>(
            @"SELECT PLANT_ID AS PlantId,
                     EQUIPMENT_ID AS EquipmentId,
                     PROCESS_LOT_ID AS ProcessLotId,
                     PROCESS_ID AS ProcessId,
                     TOTAL_QTY AS TotalQuantity,
                     DEFECT_QTY AS DefectQuantity,
                     UNIT AS Unit,
                     SOURCE AS Source,
                     SOURCE_EVENT_ID AS SourceEventId,
                     COALESCE(IS_LOT_OUTPUT,
                         CASE WHEN PROCESS_LOT_ID IS NULL THEN 0 ELSE 1 END) AS IsLotOutput
              FROM EST_EQUIPMENT_OUTPUT_EVENT
              WHERE EQUIPMENT_ID IN @equipmentIds
                AND OCCURRED_AT >= @from AND OCCURRED_AT < @to",
            new { equipmentIds, from = fromStr, to = toStr }, ct);
        return canonical
            .GroupBy(static row => new EvidenceScope(row.PlantId, row.EquipmentId))
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<CanonicalOutputRow>)group.ToArray());
    }

    private static OeeLotCounts BuildOutputCounts(
        string plantId,
        string equipmentId,
        string fromStr,
        string toStr,
        IReadOnlyList<CanonicalOutputRow> canonical,
        OeeProductionWindowDto production)
    {
        // Canonical non-LOT and LOT output can coexist with legacy POM during rollout. Compare each canonical
        // LOT against the concrete POM TrackOut multiset: projected duplicates are consumed one-for-one while
        // canonical-only LOTs remain. This avoids the old "one POM row drops every canonical LOT" behavior.
        var included = canonical
            .Where(static row => !row.IsLotOutput)
            .Select(static row => CountEvidence.FromCanonical(row))
            .ToList();
        var canonicalLots = canonical.Where(static row => row.IsLotOutput).ToArray();

        if (production.LotEventCount > 0)
        {
            if (production.LotOutputs is { Count: > 0 } pomLots)
            {
                included.AddRange(pomLots.Select(static row => CountEvidence.FromPom(row)));
                var consumedPom = new bool[pomLots.Count];
                foreach (var canonicalLot in canonicalLots)
                {
                    var duplicateIndex = FindDuplicatePom(canonicalLot, pomLots, consumedPom);
                    if (duplicateIndex >= 0)
                        consumedPom[duplicateIndex] = true;
                    else
                        included.Add(CountEvidence.FromCanonical(canonicalLot));
                }
            }
            else
            {
                // Compatibility fallback for older evidence adapters that expose only aggregate LOT counts.
                // They cannot prove which canonical LOT is a projection, so retain the legacy authority rule.
                included.Add(new CountEvidence(
                    production.LotTotalCount,
                    production.LotDefectCount,
                    SingleUnitOrThrow(production.TrackOuts.Select(static row => row.QuantityUom),
                        plantId, equipmentId, fromStr, toStr)));
            }
        }
        else
        {
            included.AddRange(canonicalLots.Select(static row => CountEvidence.FromCanonical(row)));
        }

        SingleUnitOrThrow(included.Select(static row => row.Unit),
            plantId, equipmentId, fromStr, toStr);
        return new OeeLotCounts(
            included.Sum(static row => row.TotalQuantity),
            included.Sum(static row => row.DefectQuantity));
    }

    private static int FindDuplicatePom(
        CanonicalOutputRow canonical,
        IReadOnlyList<OeeLotOutputDto> pomLots,
        IReadOnlyList<bool> consumed)
    {
        for (var i = 0; i < pomLots.Count; i++)
        {
            if (consumed[i]) continue;
            var pom = pomLots[i];
            var exactEvidence = string.Equals(canonical.Source, "POM", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(canonical.SourceEventId)
                                && string.Equals(canonical.SourceEventId, pom.EvidenceId, StringComparison.Ordinal);
            var sameBusinessEvent = string.Equals(
                                        canonical.ProcessLotId, pom.ProcessLotId, StringComparison.Ordinal)
                                    && canonical.TotalQuantity == pom.TotalQuantity
                                    && canonical.DefectQuantity == pom.DefectQuantity
                                    && (string.IsNullOrWhiteSpace(canonical.ProcessId)
                                        || string.Equals(canonical.ProcessId, pom.ProcessId, StringComparison.Ordinal))
                                    && (string.IsNullOrWhiteSpace(canonical.Unit)
                                        || string.IsNullOrWhiteSpace(pom.Unit)
                                        || string.Equals(canonical.Unit.Trim(), pom.Unit.Trim(),
                                            StringComparison.OrdinalIgnoreCase));
            if (exactEvidence || sameBusinessEvent) return i;
        }
        return -1;
    }

    private static string SingleUnitOrThrow(
        IEnumerable<string?> units,
        string plantId,
        string equipmentId,
        string from,
        string to)
    {
        var distinct = units
            .Where(static unit => !string.IsNullOrWhiteSpace(unit))
            .Select(static unit => unit!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length > 1)
        {
            throw new OeeOutputUnitMismatchException(
                plantId,
                equipmentId,
                $"OEE output window {plantId}/{equipmentId} [{from}, {to}) contains mixed units: "
                + string.Join(", ", distinct));
        }
        return distinct.FirstOrDefault() ?? string.Empty;
    }

    private static string F(DateTime dt) => dt.ToString(Ts, CultureInfo.InvariantCulture);
    private static string P(DateTime dt)
        => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    private async Task PublishAsync(AggregationBatch batch, CancellationToken ct)
    {
        if (batch.Statements.Count == 0) return;
        await _processor.ExecuteManyAsync(ct, batch.Statements.ToArray());
    }

    private async Task StartRunAsync(AggregationRun run, CancellationToken ct)
        => await _processor.ExecuteManyAsync(ct, (InsertRunSql, new
        {
            id = run.RunId,
            type = run.RunType,
            scope = run.ScopeKey,
            localDate = run.LocalDate.HasValue ? F(run.LocalDate.Value) : null,
            from = run.WindowStart.HasValue ? P(run.WindowStart.Value) : null,
            to = run.WindowEnd.HasValue ? P(run.WindowEnd.Value) : null,
            shift = (object?)run.ShiftId,
            planned = run.PlannedMinutes,
            actor = run.ActorId,
            started = P(run.StartedAt),
        }));

    private static (string Sql, object? Param) CompleteRunStatement(string runId, int affectedRows)
        => (@"
            UPDATE EST_OEE_AGGREGATION_RUN
            SET STATUS = 'Completed', AFFECTED_ROWS = @affected, COMPLETED_AT = @completed
            WHERE RUN_ID = @id AND STATUS = 'Started'", new
        {
            id = runId,
            affected = affectedRows,
            completed = P(DateTime.UtcNow),
        });

    private async Task FailRunAsync(string runId, Exception exception, CancellationToken ct)
    {
        var message = exception.Message.Length <= 1000
            ? exception.Message
            : exception.Message[..1000];
        await _processor.ExecuteManyAsync(ct, (@"
            UPDATE EST_OEE_AGGREGATION_RUN
            SET STATUS = 'Failed', ERROR_MESSAGE = @error, COMPLETED_AT = @completed
            WHERE RUN_ID = @id AND STATUS = 'Started'", new
        {
            id = runId,
            error = message,
            completed = P(DateTime.UtcNow),
        }));
    }

    private static AggregationRun NewRun(
        string runType,
        string scopeKey,
        string actorId,
        DateTime? localDate,
        DateTime? windowStart,
        DateTime? windowEnd,
        string? shiftId,
        decimal plannedMinutes)
        => new(
            "RUN_" + Guid.NewGuid().ToString("N"),
            runType,
            scopeKey,
            actorId,
            localDate,
            windowStart,
            windowEnd,
            shiftId,
            plannedMinutes,
            DateTime.UtcNow);

    private static string RequiredActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("A manual OEE aggregation requires an actor.", nameof(actorId));
        var actor = actorId.Trim();
        if (actor.Length > 50)
            throw new ArgumentException("OEE aggregation actor cannot exceed 50 characters.", nameof(actorId));
        return actor;
    }

    private static void ValidateWindow(DateTime windowStart, DateTime windowEnd)
    {
        if (windowStart == default || windowEnd == default || Utc(windowEnd) <= Utc(windowStart))
            throw new ArgumentException("OEE aggregation window end must be after start.");
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string ScopeKey(string material)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private const string InsertRunSql = @"
        INSERT INTO EST_OEE_AGGREGATION_RUN
        (RUN_ID, RUN_TYPE, SCOPE_KEY, LOCAL_DATE, WINDOW_START_UTC, WINDOW_END_UTC,
         SHIFT_ID, PLANNED_MINUTES, ACTOR_ID, STATUS, AFFECTED_ROWS, STARTED_AT)
        VALUES
        (@id, @type, @scope, @localDate, @from, @to,
         @shift, @planned, @actor, 'Started', 0, @started)";

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
    private sealed record AggregationRun(
        string RunId,
        string RunType,
        string ScopeKey,
        string ActorId,
        DateTime? LocalDate,
        DateTime? WindowStart,
        DateTime? WindowEnd,
        string? ShiftId,
        decimal PlannedMinutes,
        DateTime StartedAt);
    private sealed class AggregationBatch
    {
        public List<(string Sql, object? Param)> Statements { get; } = [];
        public int Written { get; set; }

        public void Append(AggregationBatch other)
        {
            Statements.AddRange(other.Statements);
            Written += other.Written;
        }
    }
    private sealed record EvidenceScope(string PlantId, string EquipmentId);
    private sealed record ProductionSnapshot(EvidenceScope Scope, OeeProductionWindowDto Production);
    private sealed record CountEvidence(decimal TotalQuantity, decimal DefectQuantity, string Unit)
    {
        public static CountEvidence FromCanonical(CanonicalOutputRow row)
            => new(row.TotalQuantity, row.DefectQuantity, row.Unit);

        public static CountEvidence FromPom(OeeLotOutputDto row)
            => new(row.TotalQuantity, row.DefectQuantity, row.Unit);
    }
    private sealed class CanonicalOutputRow
    {
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string? ProcessLotId { get; set; }
        public string? ProcessId { get; set; }
        public decimal TotalQuantity { get; set; }
        public decimal DefectQuantity { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? SourceEventId { get; set; }
        public bool IsLotOutput { get; set; }
    }
    private sealed class TransitionRow
    {
        public string EquipmentId { get; set; } = string.Empty;
        public string HistoryId { get; set; } = string.Empty;
        public DateTime ChangedAt { get; set; }
        public string SetState { get; set; } = string.Empty;
        public bool IsCarry { get; set; }
    }
    private sealed class OeeOutputUnitMismatchException : InvalidOperationException
    {
        public OeeOutputUnitMismatchException(string plantId, string equipmentId, string message)
            : base(message)
        {
            PlantId = plantId;
            EquipmentId = equipmentId;
        }

        public string PlantId { get; }
        public string EquipmentId { get; }
    }
    private enum GeneratedRowKind { Takt, Loss, Oee }
    private enum AggregationKind { AutomaticDay, ManualDay, RebuildWindow, ManualWindow }

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
