namespace NexaOne.EST.Application.Est;

public interface IUtilityRepository
{
    Task<UtilityMeterRecord?> GetMeterAsync(string meterId, CancellationToken ct = default);
    Task<UtilityMeterSaveCommandRecord?> GetMeterSaveCommandAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<bool> TrySaveMeterAsync(
        UtilityMeterRecord meter,
        int expectedVersion,
        string idempotencyKey,
        string requestHash,
        string actorId,
        CancellationToken ct = default);
    Task<IReadOnlyList<UtilityMeterConfigHistoryRecord>> GetMeterConfigHistoryAsync(
        string meterId, CancellationToken ct = default);
    Task<UtilityReadingRecord?> GetReadingAsync(string source, string sourceEventId, CancellationToken ct = default);
    Task<bool> TryAddReadingAsync(UtilityReadingRecord reading, CancellationToken ct = default);
    Task<UtilityMeterEventRecord?> GetMeterEventAsync(string idempotencyKey, CancellationToken ct = default);
    Task<bool> TryAddMeterEventAsync(UtilityMeterEventRecord meterEvent, CancellationToken ct = default);
    Task<IReadOnlyList<UtilityMeterEventRecord>> GetMeterEventsAsync(
        string meterId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);
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
    bool IsActive,
    int ConfigVersion = 1);

public sealed record UtilityMeterSaveCommandRecord(
    string IdempotencyKey,
    string RequestHash,
    string MeterId,
    int ExpectedVersion,
    int ResultVersion,
    string ActorId,
    DateTime CreatedAt);

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
    DateTime CreatedAt,
    int MeterConfigVersion = 1,
    string PlantId = "",
    string ReadingMode = "Cumulative",
    decimal? CostPerUnit = null,
    decimal? CarbonPerUnit = null);

public sealed record UtilityMeterEventRecord(
    string EventId,
    string IdempotencyKey,
    string RequestHash,
    string MeterId,
    string PlantId,
    string? EquipmentId,
    string EventType,
    DateTime OccurredAt,
    string Reason,
    decimal? PreviousValue,
    decimal? AfterValue,
    decimal? BaselineValue,
    string Unit,
    string ActorUserId,
    DateTime CreatedAt,
    int MeterConfigVersion = 1);

public sealed record UtilityMeterConfigHistoryRecord(
    string HistoryId,
    string MeterId,
    int ConfigVersion,
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
    bool IsActive,
    string ChangedBy,
    DateTime ChangedAt);

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
