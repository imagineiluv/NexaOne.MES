using NexaOne.CMMS.Domain;

namespace NexaOne.CMMS.Application.Cmms;

public interface IMaintenancePlanRepository
{
    Task<MaintenancePlan?> GetByIdAsync(string planId, CancellationToken ct = default);
    Task<IReadOnlyList<MaintenancePlan>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<MaintenancePlan>> GetByStatusAsync(MaintenancePlanStatus status, CancellationToken ct = default);
    Task AddAsync(MaintenancePlan plan, CancellationToken ct = default);
    Task UpdateAsync(MaintenancePlan plan, CancellationToken ct = default);
}
