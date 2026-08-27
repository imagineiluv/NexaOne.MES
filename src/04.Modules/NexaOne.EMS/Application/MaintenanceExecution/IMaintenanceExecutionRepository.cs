namespace NexaOne.EMS.Application.MaintenanceExecution;

public interface IMaintenanceExecutionRepository
{
    Task<string?> GetWorkOrderStatusAsync(string workOrderId, CancellationToken ct = default);
    Task<bool> MaintenanceItemExistsAsync(string itemId, CancellationToken ct = default);
    Task<MaintenanceCheckRecord?> GetCheckByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<bool> TryAddCheckAsync(MaintenanceCheckRecord record, CancellationToken ct = default);
    Task<MaintenanceLaborRecord?> GetLaborAsync(string laborId, CancellationToken ct = default);
    Task<MaintenanceLaborRecord?> GetLaborByStartIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<MaintenanceLaborRecord?> GetLaborByEndIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<bool> TryStartLaborAsync(MaintenanceLaborRecord record, CancellationToken ct = default);
    Task<bool> TryCompleteLaborAsync(
        MaintenanceLaborRecord record,
        int expectedVersion,
        CancellationToken ct = default);
}

public sealed record MaintenanceCheckRecord(
    string CheckResultId,
    string IdempotencyKey,
    string RequestHash,
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
    string ClientChannel,
    string? DeviceId,
    string? CorrelationId,
    DateTime CreatedAt);

public sealed record MaintenanceLaborRecord(
    string LaborId,
    string StartIdempotencyKey,
    string StartRequestHash,
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
    string StartClientChannel,
    string? StartDeviceId,
    string? EndIdempotencyKey,
    string? EndRequestHash,
    string? EndClientChannel,
    string? EndDeviceId,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);
