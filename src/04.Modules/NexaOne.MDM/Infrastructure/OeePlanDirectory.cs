using System.Globalization;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>
/// MDM 설비·공장 달력·교대·시간대를 OEE 계획 snapshot으로 변환하는 owner adapter입니다.
/// </summary>
public sealed class OeePlanDirectory : QueryRepository, IOeePlanDirectory
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public OeePlanDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<OeePlanSnapshotDto> LoadPlanAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime? localDay,
        CancellationToken ct = default)
    {
        var equipmentIds = NormalizeEquipmentIds(targetEquipmentIds);
        if (equipmentIds.Length == 0)
            return new OeePlanSnapshotDto([], []);

        var equipmentRows = (await LoadEquipmentRowsAsync(equipmentIds, ct)).ToArray();
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
            if (plantCalendar.Any(static row => string.Equals(
                    row.DayType, "Holiday", StringComparison.OrdinalIgnoreCase)))
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

    public async Task<IReadOnlyList<OeePlantLocalDateDto>> LoadPlantLocalDatesAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var equipmentIds = NormalizeEquipmentIds(targetEquipmentIds);
        if (equipmentIds.Length == 0) return [];

        var rows = await LoadEquipmentRowsAsync(equipmentIds, ct);
        var instant = utcNow.Kind switch
        {
            DateTimeKind.Utc => utcNow,
            DateTimeKind.Local => utcNow.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
        };
        return rows
            .GroupBy(static row => row.PlantId, StringComparer.Ordinal)
            .Select(group => new OeePlantLocalDateDto(
                group.Key,
                TimeZoneInfo.ConvertTimeFromUtc(
                    instant,
                    ResolveTimeZone(group.First().TimeZone)).Date))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadProductUnitsAsync(
        IReadOnlyList<string> productIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var ids = productIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var rows = await QueryAsync<ProductUnitRow>(
            @"SELECT PRODUCT_ID, COALESCE(UNIT, '') AS UNIT
              FROM MDM_PRODUCT
              WHERE PRODUCT_ID IN @ids",
            new { ids },
            ct);
        return rows.ToDictionary(
            static row => row.ProductId,
            static row => row.Unit,
            StringComparer.Ordinal);
    }

    private Task<IReadOnlyList<EquipmentRow>> LoadEquipmentRowsAsync(
        IReadOnlyList<string> equipmentIds,
        CancellationToken ct) =>
        QueryAsync<EquipmentRow>(
            @"SELECT e.EQUIPMENT_ID, e.PLANT_ID, COALESCE(p.TIME_ZONE, 'UTC') AS TIME_ZONE
              FROM MDM_EQUIPMENT e
              LEFT JOIN MDM_PLANT p ON p.PLANT_ID = e.PLANT_ID
              WHERE e.EQUIPMENT_ID IN @equipmentIds",
            new { equipmentIds }, ct);

    private static string[] NormalizeEquipmentIds(IReadOnlyList<string> targetEquipmentIds)
    {
        ArgumentNullException.ThrowIfNull(targetEquipmentIds);
        return targetEquipmentIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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

    private sealed class ProductUnitRow
    {
        public string ProductId { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
    }
}
