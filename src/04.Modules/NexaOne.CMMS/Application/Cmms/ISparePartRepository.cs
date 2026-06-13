using NexaOne.CMMS.Domain;

namespace NexaOne.CMMS.Application.Cmms;

public interface ISparePartRepository
{
    Task<SparePart?> GetByIdAsync(string partId, CancellationToken ct = default);
    Task<IReadOnlyList<SparePart>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SparePart>> GetLowStockAsync(CancellationToken ct = default);
    Task AddAsync(SparePart part, CancellationToken ct = default);
    Task UpdateAsync(SparePart part, CancellationToken ct = default);
}
