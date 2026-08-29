using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.MaintenanceSchedules;

public sealed class MaintenanceScheduleBridge : IMaintenanceScheduleBridge
{
    private readonly MaintenanceScheduleService _service;

    public MaintenanceScheduleBridge(MaintenanceScheduleService service) => _service = service;

    public async Task<Result<MaintenanceScheduleDto>> CreateAsync(
        MaintenanceScheduleCreateCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.CreateAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<MaintenanceScheduleDto>(result.Error);
    }

    public async Task<Result<MaintenanceScheduleDto>> UpdateAsync(
        MaintenanceScheduleUpdateCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.UpdateAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<MaintenanceScheduleDto>(result.Error);
    }

    public async Task<Result<MaintenanceScheduleAcknowledgementDto>> AcknowledgeAsync(
        MaintenanceScheduleAcknowledgeCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.AcknowledgeAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<MaintenanceScheduleAcknowledgementDto>(result.Error);
    }

    private static MaintenanceScheduleDto ToDto(MaintenanceScheduleRecord item) => new(
        item.ScheduleId, item.MaintenancePlanId, item.TriggerType, item.IntervalValue,
        item.IntervalUnit, item.TimeZoneId, item.LastDueAt, item.NextDueAt,
        item.MeterParameterId, item.MeterThreshold, item.MeterBaselineValue,
        item.NextMeterDueValue, item.ConditionRuleId, item.AutoCreateWorkOrder,
        item.IsActive, item.Version, item.CreatedBy, item.CreatedAt,
        item.UpdatedBy, item.UpdatedAt);

    private static MaintenanceScheduleAcknowledgementDto ToDto(
        MaintenanceScheduleAcknowledgementRecord item) => new(
        item.AcknowledgementId, item.ScheduleId, item.MaintenancePlanId, item.TriggerType,
        item.DueAt, item.NextDueAt, item.MeterDueValue, item.ObservedMeterValue,
        item.NextMeterDueValue, item.ConditionRuleId, item.ConditionMet,
        item.AcknowledgedAt, item.AcknowledgedBy, item.IdempotencyKey,
        item.ClientChannel, item.DeviceId, item.CorrelationId, item.Remark,
        item.FromVersion, item.ToVersion);
}
