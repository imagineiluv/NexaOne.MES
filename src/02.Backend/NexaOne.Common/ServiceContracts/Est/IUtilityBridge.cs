using NexaOne.Common;

namespace NexaOne.ServiceContracts.Est;

/// <summary>
/// 전력·용수·가스·CDA 등 설비 utility의 계기, 판독값, 기간 사용량을 관리한다.
/// FDC/설비 플러그인은 태그를 표준 판독값으로 변환하고 EST가 비용·탄소를 포함한 원장을 소유한다.
/// </summary>
public interface IUtilityBridge : INexaModuleBridge
{
    Task<Result<UtilityMeterDto>> SaveMeterAsync(UtilityMeterCommand command, CancellationToken ct = default);
    Task<Result<UtilityReadingDto>> RecordReadingAsync(UtilityReadingCommand command, CancellationToken ct = default);
    Task<Result<UtilityMeterEventDto>> RecordMeterEventAsync(
        UtilityMeterEventCommand command, CancellationToken ct = default);
    Task<Result<IReadOnlyList<UtilityMeterEventDto>>> GetMeterEventHistoryAsync(
        string meterId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<Result<IReadOnlyList<UtilityMeterConfigHistoryDto>>> GetMeterConfigHistoryAsync(
        string meterId, CancellationToken ct = default);
    Task<Result<UtilitySummaryDto>> SummarizeAsync(UtilitySummaryCommand command, CancellationToken ct = default);
}

public sealed record UtilityMeterCommand(
    string MeterId,
    string MeterName,
    string PlantId,
    string UtilityType,
    string Unit,
    string ReadingMode,
    decimal ScaleFactor = 1m,
    string? EquipmentId = null,
    string? FdcParameterId = null,
    decimal? CostPerUnit = null,
    decimal? CarbonPerUnit = null,
    bool IsActive = true,
    string? ActorId = null,
    int ExpectedVersion = 0,
    string IdempotencyKey = "");

public sealed record UtilityReadingCommand(
    string MeterId,
    decimal RawValue,
    string Source,
    string SourceEventId,
    DateTime RecordedAt,
    string Quality = "Good",
    string? EquipmentId = null,
    string? ProcessLotId = null,
    string? WorkOrderId = null,
    string? RecipeId = null,
    int? RecipeVersion = null,
    string? ActorId = null);

/// <summary>
/// 누적 계량기의 계수 기준이 바뀐 사건. 정확한 경계값을 알면 PreviousValue/AfterValue를 함께 보내고,
/// 이전 경계가 불명확하면 BaselineValue만 보내 새 연속 구간을 시작한다. 값은 meter의 정규화 단위다.
/// </summary>
public sealed record UtilityMeterEventCommand(
    string IdempotencyKey,
    string MeterId,
    string EventType,
    DateTime OccurredAt,
    string Reason,
    decimal? PreviousValue = null,
    decimal? AfterValue = null,
    decimal? BaselineValue = null,
    string? ActorId = null);

public sealed record UtilitySummaryCommand(
    string MeterId,
    string PeriodType,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    string? ActorId = null);

public sealed record UtilityMeterDto(
    string MeterId,
    string MeterName,
    string PlantId,
    string? EquipmentId,
    string UtilityType,
    string Unit,
    string ReadingMode,
    decimal ScaleFactor,
    decimal? CostPerUnit,
    decimal? CarbonPerUnit,
    bool IsActive,
    int ConfigVersion = 1);

public sealed record UtilityReadingDto(
    string ReadingId,
    string MeterId,
    decimal RawValue,
    decimal NormalizedValue,
    string Unit,
    string Source,
    string SourceEventId,
    string Quality,
    DateTime RecordedAt,
    string RecordedBy,
    int MeterConfigVersion = 1);

public sealed record UtilityMeterEventDto(
    string EventId,
    string IdempotencyKey,
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
    DateTime CreatedAt);

public sealed record UtilityMeterConfigHistoryDto(
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

public sealed record UtilitySummaryDto(
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
    decimal? CarbonAmount);
