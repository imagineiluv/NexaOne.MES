using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcInterlockHistoryRepository
{
    Task<IReadOnlyList<FdcInterlockHistory>> GetByEquipmentAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<IReadOnlyList<FdcInterlockHistory>> GetUnresolvedAsync(string equipmentId, CancellationToken ct = default);
    Task AddAsync(FdcInterlockHistory history, CancellationToken ct = default);
    Task UpdateAsync(FdcInterlockHistory history, CancellationToken ct = default);
}
