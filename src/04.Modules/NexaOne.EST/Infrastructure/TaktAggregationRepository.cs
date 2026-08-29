using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexaOne.EST.Domain.Takt;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Infrastructure;

/// <summary>
/// 효력 기간이 적용된 택트 목표, host evidence seam의 TrackOut snapshot, 저장된 OEE 시간가동률을 결합해
/// 설비별 택트·사이클 요약을 재생성한다. 택트 집계에서 별도 가동률은 재계산하지 않는다.
/// </summary>
public sealed class TaktAggregationRepository : QueryRepository
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly ServiceObjectProcessor _processor;

    /// <summary>택트 집계용 조회·트랜잭션 저장소를 생성한다.</summary>
    public TaktAggregationRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    /// <summary>
    /// 보고일·교대의 설비 집계 구간을 다시 계산하고 기존 요약을 원자적으로 교체한다.
    /// </summary>
    /// <returns>새로 저장한 품목·공정 단위 택트 요약 건수.</returns>
    public async Task<int> AggregateEquipmentWindowAsync(
        string oeeId,
        string plantId,
        string equipmentId,
        DateTime reportDate,
        string? shiftId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        IReadOnlyList<OeeTrackOutDto> trackOuts,
        CancellationToken ct = default)
    {
        if (windowEndUtc <= windowStartUtc)
            throw new ArgumentException("Takt aggregation window end must be after start.", nameof(windowEndUtc));

        var delete = (Sql: DeleteSummarySql, Param: (object?)new
        {
            plant = plantId,
            equipment = equipmentId,
            date = Format(reportDate.Date),
            shift = (object?)shiftId,
        });
        // OEE가 소유한 시간가동률을 단일 기준으로 사용해 지표 간 가동률 불일치를 막는다.
        var oee = await LoadOeeAvailabilityAsync(oeeId, ct);
        if (!oee.HasValue)
        {
            // 상위 OEE 결과가 사라진 재집계는 이전 택트 요약도 제거해 오래된 지표를 남기지 않는다.
            await _processor.ExecuteManyAsync(ct, delete);
            return 0;
        }

        var batch = await StageEquipmentWindowsAsync(
            [new TaktWindowInput(
                plantId, equipmentId, reportDate, shiftId, windowStartUtc, windowEndUtc,
                oee.Value, trackOuts)], ct);
        await _processor.ExecuteManyAsync(ct, batch.Statements.ToArray());
        return batch.Written;
    }

    /// <summary>
    /// 여러 설비의 택트 결과를 계산만 하고 SQL 배치를 반환한다. 호출자가 OEE/Loss와 같은 트랜잭션으로
    /// 게시할 수 있어 뒤쪽 설비 계산 실패가 앞쪽 설비 마트를 부분 갱신하지 않는다.
    /// </summary>
    internal async Task<TaktAggregationBatch> StageEquipmentWindowsAsync(
        IReadOnlyList<TaktWindowInput> windows,
        CancellationToken ct = default)
    {
        if (windows.Count == 0)
            return new TaktAggregationBatch([], 0);

        foreach (var window in windows)
        {
            if (window.WindowEndUtc <= window.WindowStartUtc)
                throw new ArgumentException("Takt aggregation window end must be after start.", nameof(windows));
        }

        var equipmentIds = windows
            .Select(static window => window.EquipmentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var earliestDate = windows.Min(static window => window.ReportDate.Date);
        var latestDate = windows.Max(static window => window.ReportDate.Date);
        var targets = await LoadTargetsAsync(equipmentIds, earliestDate, latestDate, ct);
        var targetsByScope = targets
            .GroupBy(static target => new TaktScope(target.PlantId, target.EquipmentId))
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var statements = new List<(string Sql, object? Param)>();
        var written = 0;

        foreach (var window in windows)
        {
            var effectiveAt = window.ReportDate.Date.AddDays(1).AddSeconds(-1);
            statements.Add((DeleteSummarySql, new
            {
                plant = window.PlantId,
                equipment = window.EquipmentId,
                date = Format(window.ReportDate.Date),
                shift = (object?)window.ShiftId,
            }));

            targetsByScope.TryGetValue(
                new TaktScope(window.PlantId, window.EquipmentId), out var scopedTargets);
            foreach (var target in ResolveEffectiveTargets(
                         (scopedTargets ?? [])
                         .Where(target => target.EffectiveFrom <= effectiveAt
                                          && (!target.EffectiveTo.HasValue
                                              || target.EffectiveTo.Value >= effectiveAt))
                         .Where(target => window.ShiftId is null
                             ? target.ShiftId is null
                             : target.ShiftId is null
                               || string.Equals(target.ShiftId, window.ShiftId, StringComparison.Ordinal)),
                         window.ShiftId))
            {
                var scopedFacts = window.TrackOuts
                    .Where(f => string.Equals(f.ProductId, target.ProductId, StringComparison.Ordinal)
                                && string.Equals(f.ProcessId, target.ProcessId, StringComparison.Ordinal))
                    .ToList();
                if (scopedFacts.Any(f => !string.Equals(
                        f.QuantityUom, target.QuantityUom, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"TrackOut UOM does not match takt target {target.TargetId} ({target.QuantityUom}).");
                }

                // TrackIn 시각이 없는 TrackOut도 생산 수량에는 포함하되, 실제 사이클 시간 산정에서는 제외한다.
                var actualQty = scopedFacts.Sum(f => f.Qty);
                var measurable = scopedFacts
                    .Where(f => f.TrackInTimeUtc.HasValue && f.TrackOutTimeUtc > f.TrackInTimeUtc.Value)
                    .ToList();
                var measuredQty = measurable.Sum(f => f.Qty);
                var actualRunSeconds = measurable.Sum(
                    f => (decimal)(f.TrackOutTimeUtc - f.TrackInTimeUtc!.Value).TotalSeconds);
                var result = TaktTimeCalculator.Compute(
                    new TaktTargetDefinition(
                        target.NetAvailableSeconds, target.RequiredQty,
                        target.IdealCycleSecondsPerUnit, target.QuantityUom, target.TimeUom),
                    new TaktActuals(actualQty, measuredQty, actualRunSeconds, target.QuantityUom),
                    window.Availability);

                statements.Add((InsertSummarySql, new
                {
                    id = DeterministicId(
                        target.TargetId, window.EquipmentId, window.ReportDate.Date, window.ShiftId),
                    target = target.TargetId,
                    date = Format(window.ReportDate.Date),
                    from = Format(window.WindowStartUtc),
                    to = Format(window.WindowEndUtc),
                    plant = window.PlantId,
                    product = target.ProductId,
                    process = target.ProcessId,
                    equipment = window.EquipmentId,
                    shift = (object?)window.ShiftId,
                    requiredQty = target.RequiredQty,
                    actualQty = result.ActualQty,
                    measuredQty = result.MeasuredQty,
                    netSeconds = target.NetAvailableSeconds,
                    runSeconds = result.ActualRunSeconds,
                    targetTakt = result.TargetTaktSecondsPerUnit,
                    idealCycle = result.IdealCycleSecondsPerUnit,
                    actualCycle = result.ActualCycleSecondsPerUnit,
                    deviationSeconds = result.DeviationSecondsPerUnit,
                    deviationRatio = result.DeviationRatio,
                    availability = result.AvailabilityRatio,
                    quantityUom = result.QuantityUom,
                    timeUom = result.TimeUom,
                }));
                written++;
            }
        }

        return new TaktAggregationBatch(statements, written);
    }

    private async Task<decimal?> LoadOeeAvailabilityAsync(string oeeId, CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT AVAILABILITY FROM EST_OEE_SUMMARY WHERE OEE_ID = @id",
            new { id = oeeId }, ct);
        var row = rows.Select(ToDictionary).FirstOrDefault();
        return row is null ? null : Decimal(row, "AVAILABILITY");
    }

    private async Task<List<TargetRow>> LoadTargetsAsync(
        IReadOnlyList<string> equipmentIds,
        DateTime earliestDate,
        DateTime latestDate,
        CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            @"SELECT TAKT_TARGET_ID, PLANT_ID, EQUIPMENT_ID, PRODUCT_ID, PROCESS_ID, SHIFT_ID,
                     EFFECTIVE_FROM, EFFECTIVE_TO,
                     REQUIRED_QTY, NET_AVAILABLE_SECONDS, IDEAL_CYCLE_SECONDS_PER_UNIT,
                     QUANTITY_UOM, TIME_UOM
              FROM EST_TAKT_TARGET
              WHERE IS_ACTIVE = 1 AND EQUIPMENT_ID IN @equipmentIds
                AND EFFECTIVE_FROM < @latestEnd
                AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO >= @earliest)",
            new
            {
                equipmentIds,
                earliest = Format(earliestDate.Date),
                latestEnd = Format(latestDate.Date.AddDays(1)),
            }, ct);
        return rows.Select(ToDictionary).Select(r => new TargetRow(
            String(r, "TAKT_TARGET_ID"), String(r, "PLANT_ID"), String(r, "EQUIPMENT_ID"),
            String(r, "PRODUCT_ID"), String(r, "PROCESS_ID"), NullableString(r, "SHIFT_ID"),
            Date(r, "EFFECTIVE_FROM"), NullableDate(r, "EFFECTIVE_TO"),
            Decimal(r, "REQUIRED_QTY"), Decimal(r, "NET_AVAILABLE_SECONDS"),
            Decimal(r, "IDEAL_CYCLE_SECONDS_PER_UNIT"), String(r, "QUANTITY_UOM"),
            String(r, "TIME_UOM"))).ToList();
    }

    private static IEnumerable<TargetRow> ResolveEffectiveTargets(
        IEnumerable<TargetRow> targets, string? shiftId)
        // 교대 전용 목표가 전일 목표보다 우선하며, 같은 범위에서는 가장 최근 효력 리비전을 선택한다.
        => targets
            .GroupBy(t => new { t.ProductId, t.ProcessId })
            .Select(g => g
                .OrderByDescending(t => string.Equals(t.ShiftId, shiftId, StringComparison.Ordinal))
                .ThenByDescending(t => t.EffectiveFrom)
                .ThenBy(t => t.TargetId, StringComparer.Ordinal)
                .First());

    private const string DeleteSummarySql = @"
        DELETE FROM EST_TAKT_SUMMARY
        WHERE TAKT_SUMMARY_ID LIKE 'TKT_%' AND PLANT_ID = @plant AND EQUIPMENT_ID = @equipment
          AND TAKT_DATE = @date
          AND ((@shift IS NULL AND SHIFT_ID IS NULL) OR SHIFT_ID = @shift)";

    private const string InsertSummarySql = @"
        INSERT INTO EST_TAKT_SUMMARY
        (TAKT_SUMMARY_ID, TAKT_TARGET_ID, TAKT_DATE, WINDOW_START_UTC, WINDOW_END_UTC,
         PLANT_ID, PRODUCT_ID, PROCESS_ID, EQUIPMENT_ID, SHIFT_ID,
         REQUIRED_QTY, ACTUAL_QTY, MEASURED_QTY, NET_AVAILABLE_SECONDS, ACTUAL_RUN_SECONDS,
         TARGET_TAKT_SECONDS_PER_UNIT, IDEAL_CYCLE_SECONDS_PER_UNIT, ACTUAL_CYCLE_SECONDS_PER_UNIT,
         DEVIATION_SECONDS_PER_UNIT, DEVIATION_RATIO, AVAILABILITY_RATIO, QUANTITY_UOM, TIME_UOM)
        VALUES
        (@id, @target, @date, @from, @to, @plant, @product, @process, @equipment, @shift,
         @requiredQty, @actualQty, @measuredQty, @netSeconds, @runSeconds,
         @targetTakt, @idealCycle, @actualCycle, @deviationSeconds, @deviationRatio,
         @availability, @quantityUom, @timeUom)";

    private static string DeterministicId(string targetId, string equipmentId, DateTime date, string? shiftId)
    {
        // 재집계해도 동일한 논리 키가 나오도록 해시하여 재시도·재실행 결과를 추적 가능하게 유지한다.
        var material = $"{targetId}|{equipmentId}|{date:yyyyMMdd}|{shiftId ?? "ALLDAY"}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return "TKT_" + hash[..32];
    }

    private static string Format(DateTime value)
        => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    private static IDictionary<string, object> ToDictionary(dynamic row)
        => (IDictionary<string, object>)row;
    private static string String(IDictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? value.ToString()! : string.Empty;
    private static string? NullableString(IDictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? value.ToString() : null;
    private static decimal Decimal(IDictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not DBNull
            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : 0m;
    private static DateTime Date(IDictionary<string, object> row, string key)
        => NullableDate(row, key) ?? default;
    private static DateTime? NullableDate(IDictionary<string, object> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null or DBNull) return null;
        if (value is DateTime date) return date;
        return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : null;
    }

    internal sealed record TaktWindowInput(
        string PlantId,
        string EquipmentId,
        DateTime ReportDate,
        string? ShiftId,
        DateTime WindowStartUtc,
        DateTime WindowEndUtc,
        decimal Availability,
        IReadOnlyList<OeeTrackOutDto> TrackOuts);
    internal sealed record TaktAggregationBatch(
        IReadOnlyList<(string Sql, object? Param)> Statements,
        int Written);
    private sealed record TaktScope(string PlantId, string EquipmentId);
    private sealed record TargetRow(
        string TargetId, string PlantId, string EquipmentId,
        string ProductId, string ProcessId, string? ShiftId, DateTime EffectiveFrom, DateTime? EffectiveTo,
        decimal RequiredQty, decimal NetAvailableSeconds, decimal IdealCycleSecondsPerUnit,
        string QuantityUom, string TimeUom);
}
