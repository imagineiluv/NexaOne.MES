using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcParameterRepository
{
    Task<FdcParameter?> GetByIdAsync(string parameterId, CancellationToken ct = default);
    Task<IReadOnlyList<FdcParameter>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default);
    Task AddAsync(FdcParameter parameter, CancellationToken ct = default);
    Task UpdateAsync(FdcParameter parameter, CancellationToken ct = default);
}
