using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcParameterGroupRepository
{
    Task<FdcParameterGroup?> GetByIdAsync(string groupId, CancellationToken ct = default);
    Task<IReadOnlyList<FdcParameterGroup>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task AddAsync(FdcParameterGroup group, CancellationToken ct = default);
    Task UpdateAsync(FdcParameterGroup group, CancellationToken ct = default);
}
