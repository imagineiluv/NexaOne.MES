using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcInterlockRuleRepository
{
    Task<IReadOnlyList<FdcInterlockRule>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<FdcInterlockRule>> GetActiveRulesAsync(string equipmentId, string parameterId, CancellationToken ct = default);
    Task AddAsync(FdcInterlockRule rule, CancellationToken ct = default);
    Task UpdateAsync(FdcInterlockRule rule, CancellationToken ct = default);
}
