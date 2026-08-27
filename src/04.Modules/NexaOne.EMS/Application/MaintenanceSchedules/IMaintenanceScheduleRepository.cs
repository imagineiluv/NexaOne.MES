namespace NexaOne.EMS.Application.MaintenanceSchedules;

public interface IMaintenanceScheduleRepository
{
    /// <summary>True only when the referenced plan exists and is a preventive-maintenance (PM) plan.</summary>
    Task<bool> MaintenancePlanExistsAsync(string maintenancePlanId, CancellationToken ct = default);
    Task<MaintenanceScheduleRecord?> GetAsync(string scheduleId, CancellationToken ct = default);
    Task<bool> TryCreateAsync(MaintenanceScheduleRecord schedule, CancellationToken ct = default);
    Task<bool> TryUpdateAsync(
        MaintenanceScheduleRecord schedule,
        int expectedVersion,
        CancellationToken ct = default);
    Task<MaintenanceScheduleAcknowledgementRecord?> GetAcknowledgementAsync(
        string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> TryAcknowledgeAsync(
        MaintenanceScheduleRecord schedule,
        int expectedVersion,
        MaintenanceScheduleAcknowledgementRecord acknowledgement,
        CancellationToken ct = default);
}

public sealed record MaintenanceScheduleRecord(
    string ScheduleId,
    string MaintenancePlanId,
    string TriggerType,
    decimal? IntervalValue,
    string? IntervalUnit,
    string TimeZoneId,
    DateTime? LastDueAt,
    DateTime? NextDueAt,
    string? MeterParameterId,
    decimal? MeterThreshold,
    decimal? MeterBaselineValue,
    decimal? NextMeterDueValue,
    string? ConditionRuleId,
    bool AutoCreateWorkOrder,
    bool IsActive,
    int Version,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record MaintenanceScheduleAcknowledgementRecord(
    string AcknowledgementId,
    string ScheduleId,
    string MaintenancePlanId,
    string TriggerType,
    DateTime? DueAt,
    DateTime? NextDueAt,
    decimal? MeterDueValue,
    decimal? ObservedMeterValue,
    decimal? NextMeterDueValue,
    string? ConditionRuleId,
    bool? ConditionMet,
    DateTime AcknowledgedAt,
    string AcknowledgedBy,
    string? Remark,
    string IdempotencyKey,
    string RequestHash,
    string ClientChannel,
    string? DeviceId,
    string? CorrelationId,
    int FromVersion,
    int ToVersion,
    DateTime CreatedAt);
