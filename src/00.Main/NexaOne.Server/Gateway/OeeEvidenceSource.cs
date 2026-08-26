using System.Globalization;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.Server.Gateway;

/// <summary>
/// OEE 외부 증거의 호스트 orchestration adapter입니다. 호스트 조립 루트만 MDM/POM
/// 물리 스키마를 알고, EST plugin에는 <see cref="IOeeEvidenceSource"/>의 안정된 snapshot만
/// 전달합니다. 이로써 plugin ALC 사이에 다른 업무 모듈 implementation 참조를 만들지 않습니다.
/// </summary>
public sealed class OeeEvidenceSource : QueryRepository, IOeeEvidenceSource
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public OeeEvidenceSource(EesDataSource dataSource) : base(dataSource) { }

    public async Task<OeePlanSnapshotDto> LoadPlanAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime? localDay,
        CancellationToken ct = default)
    {
        var equipmentIds = targetEquipmentIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (equipmentIds.Length == 0)
            return new OeePlanSnapshotDto([], []);

        var equipmentRows = await QueryAsync<EquipmentRow>(
            @"SELECT e.EQUIPMENT_ID, e.PLANT_ID, COALESCE(p.TIME_ZONE, 'UTC') AS TIME_ZONE
              FROM MDM_EQUIPMENT e
              LEFT JOIN MDM_PLANT p ON p.PLANT_ID = e.PLANT_ID
              WHERE e.EQUIPMENT_ID IN @equipmentIds",
            new { equipmentIds }, ct);

        var scopes = equipmentRows
            .Select(static row => new OeeEquipmentScopeDto(row.EquipmentId, row.PlantId))
            .ToArray();
        if (!localDay.HasValue || scopes.Length == 0)
            return new OeePlanSnapshotDto(scopes, []);

        var plantIds = equipmentRows
            .Select(static row => row.PlantId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var day = localDay.Value.Date;
        var calendarRows = plantIds.Length == 0
            ? Array.Empty<CalendarRow>()
            : (await QueryAsync<CalendarRow>(
                @"SELECT c.PLANT_ID, c.DAY_TYPE, s.SHIFT_ID, s.START_TIME, s.END_TIME
                  FROM MDM_WORK_CALENDAR c
                  LEFT JOIN MDM_SHIFT s ON s.SHIFT_ID = c.SHIFT_ID AND s.IS_ACTIVE = 1
                  WHERE c.CALENDAR_DATE >= @day AND c.CALENDAR_DATE < @next
                    AND c.PLANT_ID IN @plantIds",
                new { day = Format(day), next = Format(day.AddDays(1)), plantIds }, ct)).ToArray();
        var fallbackShifts = (await QueryAsync<ShiftRow>(
            "SELECT SHIFT_ID, START_TIME, END_TIME FROM MDM_SHIFT WHERE IS_ACTIVE = 1",
            null, ct)).ToArray();

        var timeZones = equipmentRows
            .GroupBy(static row => row.PlantId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(row => row.TimeZone).FirstOrDefault() ?? "UTC",
                StringComparer.Ordinal);
        var plantDays = new List<OeePlantDayDto>(plantIds.Length);
        foreach (var plantId in plantIds)
        {
            var plantCalendar = calendarRows
                .Where(row => string.Equals(row.PlantId, plantId, StringComparison.Ordinal))
                .ToArray();
            var isHoliday = plantCalendar.Any(row => string.Equals(
                row.DayType, "Holiday", StringComparison.OrdinalIgnoreCase));
            if (isHoliday)
            {
                plantDays.Add(new OeePlantDayDto(plantId, true, []));
                continue;
            }

            IEnumerable<ShiftRow> shifts = plantCalendar.Length == 0
                ? fallbackShifts
                : plantCalendar
                    .Where(static row => !string.IsNullOrWhiteSpace(row.ShiftId))
                    .Select(static row => new ShiftRow
                    {
                        ShiftId = row.ShiftId!,
                        StartTime = row.StartTime,
                        EndTime = row.EndTime,
                    });
            var timeZone = ResolveTimeZone(timeZones.GetValueOrDefault(plantId, "UTC"));
            var windows = shifts
                .Select(shift => ResolveWindow(day, shift, timeZone))
                .Where(static window => window is not null)
                .Select(static window => window!)
                .ToArray();
            plantDays.Add(new OeePlantDayDto(plantId, false, windows));
        }

        return new OeePlanSnapshotDto(scopes, plantDays);
    }

    public async Task<OeeProductionWindowDto> LoadProductionAsync(
        string plantId,
        string equipmentId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("Production window end must be after start.", nameof(toUtc));

        var rows = await QueryAsync<TrackOutRow>(
            @"SELECT l.PRODUCT_ID, h.PROCESS_ID, h.QTY, h.DEFECT_QTY,
                     h.TRACK_IN_TIME, h.TRACK_OUT_TIME, COALESCE(p.UNIT, '') AS QUANTITY_UOM
              FROM POM_LOT_HISTORY h
              JOIN POM_LOT l ON l.PLANT_ID = h.PLANT_ID AND l.LOT_ID = h.LOT_ID
              LEFT JOIN MDM_PRODUCT p ON p.PRODUCT_ID = l.PRODUCT_ID
              WHERE h.EXECUTION_ID = 'TrackOut' AND h.PLANT_ID = @plant
                AND h.EQUIPMENT_ID = @equipment
                AND h.TRACK_OUT_TIME >= @from AND h.TRACK_OUT_TIME < @to",
            new
            {
                plant = plantId,
                equipment = equipmentId,
                from = Format(fromUtc),
                to = Format(toUtc),
            }, ct);
        var trackOuts = rows.Select(static row => new OeeTrackOutDto(
            row.ProductId,
            row.ProcessId,
            row.Qty,
            row.TrackInTime,
            row.TrackOutTime,
            row.QuantityUom)).ToArray();

        return new OeeProductionWindowDto(
            rows.Count,
            rows.Sum(static row => row.Qty),
            rows.Sum(static row => row.DefectQty),
            trackOuts);
    }

    private static OeeShiftWindowDto? ResolveWindow(DateTime day, ShiftRow shift, TimeZoneInfo timeZone)
    {
        if (!TryTime(shift.StartTime, out var start) || !TryTime(shift.EndTime, out var end))
            return null;

        var localStart = DateTime.SpecifyKind(day.Date + start, DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(day.Date + end, DateTimeKind.Unspecified);
        if (localEnd <= localStart) localEnd = localEnd.AddDays(1);
        var startUtc = ToUtc(localStart, timeZone);
        var endUtc = ToUtc(localEnd, timeZone);
        var planned = (decimal)(endUtc - startUtc).TotalMinutes;
        return planned <= 0m
            ? null
            : new OeeShiftWindowDto(shift.ShiftId, startUtc, endUtc, planned);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    private static bool TryTime(string value, out TimeSpan time)
        => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time)
           && time >= TimeSpan.Zero
           && time < TimeSpan.FromDays(1);

    private static string Format(DateTime value)
        => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private sealed class EquipmentRow
    {
        public string EquipmentId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string TimeZone { get; set; } = "UTC";
    }

    private class ShiftRow
    {
        public string ShiftId { get; set; } = string.Empty;
        public string StartTime { get; set; } = string.Empty;
        public string EndTime { get; set; } = string.Empty;
    }

    private sealed class CalendarRow : ShiftRow
    {
        public string PlantId { get; set; } = string.Empty;
        public string DayType { get; set; } = string.Empty;
    }

    private sealed class TrackOutRow
    {
        public string ProductId { get; set; } = string.Empty;
        public string ProcessId { get; set; } = string.Empty;
        public decimal Qty { get; set; }
        public decimal DefectQty { get; set; }
        public DateTime? TrackInTime { get; set; }
        public DateTime TrackOutTime { get; set; }
        public string QuantityUom { get; set; } = string.Empty;
    }
}
