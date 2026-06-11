using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public interface ISpcParamRepository
{
    Task<SpcParam?> GetByIdAsync(string paramId, CancellationToken ct = default);
    Task<IReadOnlyList<SpcParam>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task AddAsync(SpcParam param, CancellationToken ct = default);
    Task UpdateAsync(SpcParam param, CancellationToken ct = default);
}
