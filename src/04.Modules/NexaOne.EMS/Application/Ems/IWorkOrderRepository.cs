using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> GetByIdAsync(string woId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrder>> GetByEquipmentAsync(string equipmentId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrder>> GetByStatusAsync(WorkOrderStatus status, CancellationToken ct = default);
    Task<int> GetCountByStatusAsync(WorkOrderStatus status, CancellationToken ct = default);
    Task AddAsync(WorkOrder wo, CancellationToken ct = default);
    Task UpdateAsync(WorkOrder wo, CancellationToken ct = default);
}
