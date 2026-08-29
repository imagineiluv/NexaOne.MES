using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> GetByIdAsync(string woId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrder>> GetByEquipmentAsync(string equipmentId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrder>> GetByStatusAsync(WorkOrderStatus status, CancellationToken ct = default);
    Task<int> GetCountByStatusAsync(WorkOrderStatus status, CancellationToken ct = default);
    Task<bool> HasOpenLaborAsync(string woId, CancellationToken ct = default);
    Task<MaintenanceAction?> GetActionByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);
    Task<WorkOrderCreateCommandRecord?> GetCreateCommandAsync(
        string idempotencyKey,
        CancellationToken ct = default);
    Task AddAsync(WorkOrder wo, CancellationToken ct = default);
    Task UpdateAsync(WorkOrder wo, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts the work order and its Create action. Returns false when an existing
    /// work-order/idempotency winner prevented the write; the caller must reload the ledger.
    /// </summary>
    Task<bool> AddWithActionAsync(
        WorkOrder wo,
        MaintenanceAction action,
        CancellationToken ct = default);
    Task<bool> AddWithActionAsync(
        WorkOrder wo,
        MaintenanceAction action,
        WorkOrderCreateCommandRecord command,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically applies the status guard and appends its action. Returns false for a lost status
    /// guard or the known idempotency unique race; the caller must reload the ledger.
    /// </summary>
    Task<bool> UpdateWithActionAsync(
        WorkOrder wo,
        MaintenanceAction action,
        CancellationToken ct = default);
}

public sealed record WorkOrderCreateCommandRecord(
    string CommandId,
    string IdempotencyKey,
    string RequestHash,
    string WorkOrderId,
    string EquipmentId,
    string WorkOrderType,
    string Description,
    string AssigneeId,
    string? MaintenancePlanId,
    DateTime IssuedAt,
    string ActorId,
    string Source,
    string ClientChannel,
    string? DeviceId,
    string? CorrelationId,
    DateTime CreatedAt);
