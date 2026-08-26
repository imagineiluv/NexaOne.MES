using NexaOne.Common;

namespace NexaOne.ServiceContracts.Est;

/// <summary>
/// 전력·용수·가스·CDA 등 설비 utility의 계기, 판독값, 기간 사용량을 관리한다.
/// FDC/설비 플러그인은 태그를 표준 판독값으로 변환하고 EST가 비용·탄소를 포함한 원장을 소유한다.
/// </summary>
[NexaModuleBridge("Est", "utilityBridge")]
public interface IUtilityBridge : INexaModuleBridge
{
    Task<Result<UtilityMeterDto>> SaveMeterAsync(UtilityMeterCommand command, CancellationToken ct = default);
    Task<Result<UtilityReadingDto>> RecordReadingAsync(UtilityReadingCommand command, CancellationToken ct = default);
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
    string? ActorId = null);

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
    bool IsActive);

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
    string RecordedBy);

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
