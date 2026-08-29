using NexaOne.Common;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Est;

public sealed class UtilityBridge : IUtilityBridge
{
    private readonly UtilityService _service;
    public UtilityBridge(UtilityService service) => _service = service;

    public async Task<Result<UtilityMeterDto>> SaveMeterAsync(UtilityMeterCommand command, CancellationToken ct = default)
    {
        var result = await _service.SaveMeterAsync(command, ct);
        return result.IsSuccess ? Result.Success(ToDto(result.Value)) : Result.Failure<UtilityMeterDto>(result.Error);
    }

    public async Task<Result<UtilityReadingDto>> RecordReadingAsync(UtilityReadingCommand command, CancellationToken ct = default)
    {
        var result = await _service.RecordReadingAsync(command, ct);
        return result.IsSuccess ? Result.Success(ToDto(result.Value)) : Result.Failure<UtilityReadingDto>(result.Error);
    }

    public async Task<Result<UtilityMeterEventDto>> RecordMeterEventAsync(
        UtilityMeterEventCommand command, CancellationToken ct = default)
    {
        var result = await _service.RecordMeterEventAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<UtilityMeterEventDto>(result.Error);
    }

    public async Task<Result<IReadOnlyList<UtilityMeterEventDto>>> GetMeterEventHistoryAsync(
        string meterId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var result = await _service.GetMeterEventHistoryAsync(meterId, from, to, ct);
        return result.IsSuccess
            ? Result.Success<IReadOnlyList<UtilityMeterEventDto>>(result.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<UtilityMeterEventDto>>(result.Error);
    }

    public async Task<Result<IReadOnlyList<UtilityMeterConfigHistoryDto>>> GetMeterConfigHistoryAsync(
        string meterId, CancellationToken ct = default)
    {
        var result = await _service.GetMeterConfigHistoryAsync(meterId, ct);
        return result.IsSuccess
            ? Result.Success<IReadOnlyList<UtilityMeterConfigHistoryDto>>(
                result.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<UtilityMeterConfigHistoryDto>>(result.Error);
    }

    public async Task<Result<UtilitySummaryDto>> SummarizeAsync(UtilitySummaryCommand command, CancellationToken ct = default)
    {
        var result = await _service.SummarizeAsync(command, ct);
        return result.IsSuccess ? Result.Success(ToDto(result.Value)) : Result.Failure<UtilitySummaryDto>(result.Error);
    }

    private static UtilityMeterDto ToDto(UtilityMeterRecord r) => new(
        r.MeterId, r.MeterName, r.PlantId, r.EquipmentId, r.UtilityType, r.Unit,
        r.ReadingMode, r.ScaleFactor, r.CostPerUnit, r.CarbonPerUnit, r.IsActive,
        r.ConfigVersion);

    private static UtilityReadingDto ToDto(UtilityReadingRecord r) => new(
        r.ReadingId, r.MeterId, r.RawValue, r.NormalizedValue, r.Unit, r.Source,
        r.SourceEventId, r.Quality, r.RecordedAt, r.RecordedBy, r.MeterConfigVersion);

    private static UtilityMeterEventDto ToDto(UtilityMeterEventRecord r) => new(
        r.EventId, r.IdempotencyKey, r.MeterId, r.PlantId, r.EquipmentId, r.EventType,
        r.OccurredAt, r.Reason, r.PreviousValue, r.AfterValue, r.BaselineValue,
        r.Unit, r.ActorUserId, r.CreatedAt);

    private static UtilityMeterConfigHistoryDto ToDto(UtilityMeterConfigHistoryRecord r) => new(
        r.HistoryId, r.MeterId, r.ConfigVersion, r.MeterName, r.PlantId,
        r.EquipmentId, r.UtilityType, r.Unit, r.FdcParameterId, r.ReadingMode,
        r.ScaleFactor, r.CostPerUnit, r.CarbonPerUnit, r.IsActive,
        r.ChangedBy, r.ChangedAt);

    private static UtilitySummaryDto ToDto(UtilitySummaryRecord r) => new(
        r.SummaryId, r.MeterId, r.PlantId, r.EquipmentId, r.PeriodType, r.PeriodStart,
        r.PeriodEnd, r.StartReading, r.EndReading, r.Consumption, r.Unit, r.CostAmount, r.CarbonAmount);
}
