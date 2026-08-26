using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ems;

/// <summary>
/// 수동 운전 단계의 PM Calendar/Meter/Condition 스케줄 상태와 도래 확인 원장을 관리한다.
/// 조회는 EMS 파일 쿼리 레지스트리가 소유하고 이 브리지는 검증이 필요한 쓰기만 노출한다.
/// </summary>
[NexaModuleBridge("Ems", "maintenanceScheduleBridge")]
public interface IMaintenanceScheduleBridge : INexaModuleBridge
{
    Task<Result<MaintenanceScheduleDto>> CreateAsync(
        MaintenanceScheduleCreateCommand command,
        CancellationToken ct = default);

    Task<Result<MaintenanceScheduleDto>> UpdateAsync(
        MaintenanceScheduleUpdateCommand command,
        CancellationToken ct = default);

    Task<Result<MaintenanceScheduleAcknowledgementDto>> AcknowledgeAsync(
        MaintenanceScheduleAcknowledgeCommand command,
        CancellationToken ct = default);
}

public sealed record MaintenanceScheduleCreateCommand(
    string ScheduleId,
    string MaintenancePlanId,
    string TriggerType,
    decimal? IntervalValue = null,
    string? IntervalUnit = null,
    string TimeZoneId = "Asia/Seoul",
    DateTime? NextDueAt = null,
    string? MeterParameterId = null,
    decimal? MeterThreshold = null,
    decimal? MeterBaselineValue = null,
    decimal? NextMeterDueValue = null,
    string? ConditionRuleId = null,
    bool AutoCreateWorkOrder = false,
    bool IsActive = true,
    string? ActorId = null);

public sealed record MaintenanceScheduleUpdateCommand(
    string ScheduleId,
    int ExpectedVersion,
    string MaintenancePlanId,
    string TriggerType,
    decimal? IntervalValue = null,
    string? IntervalUnit = null,
    string TimeZoneId = "Asia/Seoul",
    DateTime? NextDueAt = null,
    string? MeterParameterId = null,
    decimal? MeterThreshold = null,
    decimal? MeterBaselineValue = null,
    decimal? NextMeterDueValue = null,
    string? ConditionRuleId = null,
    bool AutoCreateWorkOrder = false,
    bool IsActive = true,
    string? ActorId = null);

public sealed record MaintenanceScheduleAcknowledgeCommand(
    string ScheduleId,
    int ExpectedVersion,
    string IdempotencyKey,
    DateTime AcknowledgedAt,
    decimal? ObservedMeterValue = null,
    bool? ConditionMet = null,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null,
    string? Remark = null,
    string? ActorId = null);

public sealed record MaintenanceScheduleDto(
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

public sealed record MaintenanceScheduleAcknowledgementDto(
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
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId,
    string? CorrelationId,
    string? Remark,
    int FromVersion,
    int ToVersion);
