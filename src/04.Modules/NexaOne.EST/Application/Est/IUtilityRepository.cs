namespace NexaOne.EST.Application.Est;

public interface IUtilityRepository
{
    Task<UtilityMeterRecord?> GetMeterAsync(string meterId, CancellationToken ct = default);
    Task SaveMeterAsync(UtilityMeterRecord meter, string actorId, CancellationToken ct = default);
    Task<UtilityReadingRecord?> GetReadingAsync(string source, string sourceEventId, CancellationToken ct = default);
    Task<bool> TryAddReadingAsync(UtilityReadingRecord reading, CancellationToken ct = default);
    Task<IReadOnlyList<UtilityReadingRecord>> GetPeriodReadingsAsync(
        string meterId, DateTime from, DateTime to, bool includeBaseline, CancellationToken ct = default);
    Task SaveSummaryAsync(UtilitySummaryRecord summary, string actorId, CancellationToken ct = default);
}

public sealed record UtilityMeterRecord(
    string MeterId,
    string MeterName,
    string PlantId,
    string? EquipmentId,
    string UtilityType,
    string Unit,
    string? FdcParameterId,
    string ReadingMode,
    decimal ScaleFactor,
    decimal? CostPerUnit,
    decimal? CarbonPerUnit,
    bool IsActive);

public sealed record UtilityReadingRecord(
    string ReadingId,
    string MeterId,
    string? EquipmentId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? RecipeId,
    int? RecipeVersion,
    decimal RawValue,
    decimal NormalizedValue,
    string Unit,
    string Source,
    string SourceEventId,
    string RequestHash,
    string Quality,
    DateTime RecordedAt,
    string RecordedBy,
    DateTime CreatedAt);

public sealed record UtilitySummaryRecord(
    string SummaryId,
    string MeterId,
    string PlantId,
    string? EquipmentId,
    string PeriodType,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    decimal? StartReading,
    decimal? EndReading,
    decimal Consumption,
    string Unit,
    decimal? CostAmount,
    decimal? CarbonAmount,
    DateTime CreatedAt);
