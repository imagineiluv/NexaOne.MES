using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ems;

/// <summary>
/// Manual maintenance execution ledger. It records authenticated checklist and labor evidence for
/// an existing EMS work order; scheduling and work-order lifecycle remain separate modules.
/// </summary>
public interface IMaintenanceExecutionBridge : INexaModuleBridge
{
    Task<Result<MaintenanceCheckDto>> RecordCheckAsync(
        MaintenanceCheckCommand command,
        CancellationToken ct = default);

    Task<Result<MaintenanceLaborDto>> StartLaborAsync(
        MaintenanceLaborStartCommand command,
        CancellationToken ct = default);

    Task<Result<MaintenanceLaborDto>> CompleteLaborAsync(
        MaintenanceLaborCompleteCommand command,
        CancellationToken ct = default);
}

public sealed record MaintenanceCheckCommand(
    string CheckResultId,
    string WorkOrderId,
    int ItemSequence,
    string CheckName,
    DateTime RecordedAt,
    EmsCommandContextDto Command,
    string? ItemId = null,
    decimal? MeasuredValue = null,
    string? AttributeValue = null,
    string? Unit = null,
    bool? IsPass = null,
    string? Finding = null);

public sealed record MaintenanceLaborStartCommand(
    string LaborId,
    string WorkOrderId,
    string LaborType,
    DateTime StartedAt,
    EmsCommandContextDto Command,
    string? WorkerId = null,
    string? Remark = null);

public sealed record MaintenanceLaborCompleteCommand(
    string LaborId,
    int ExpectedVersion,
    DateTime EndedAt,
    EmsCommandContextDto Command,
    string? Remark = null);

public sealed record MaintenanceCheckDto(
    string CheckResultId,
    string WorkOrderId,
    string? ItemId,
    int ItemSequence,
    string CheckName,
    decimal? MeasuredValue,
    string? AttributeValue,
    string? Unit,
    bool? IsPass,
    string? Finding,
    string RecordedBy,
    DateTime RecordedAt,
    string? CorrelationId);

public sealed record MaintenanceLaborDto(
    string LaborId,
    string WorkOrderId,
    string UserId,
    string? WorkerId,
    string LaborType,
    DateTime StartedAt,
    DateTime? EndedAt,
    string? EndedBy,
    decimal? LaborHours,
    string? Remark,
    string? CorrelationId,
    int Version);
