using NexaOne.CMMS.Domain;

namespace NexaOne.CMMS.Application.Cmms;

public interface IMaintenancePlanRepository
{
    Task<MaintenancePlan?> GetByIdAsync(string planId, CancellationToken ct = default);
    Task<IReadOnlyList<MaintenancePlan>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<MaintenancePlan>> GetByStatusAsync(MaintenancePlanStatus status, CancellationToken ct = default);
    /// <summary>예정일(SCHEDULED_DATE)이 <paramref name="asOf"/> 이하이고 완료/취소 상태가 아닌 계획을 SCHEDULED_DATE 오름차순으로 반환한다(예방정비 도래 점검용).</summary>
    Task<IReadOnlyList<MaintenancePlan>> GetDueAsync(DateTime asOf, CancellationToken ct = default);
    Task AddAsync(MaintenancePlan plan, CancellationToken ct = default);
    Task UpdateAsync(MaintenancePlan plan, CancellationToken ct = default);
}
