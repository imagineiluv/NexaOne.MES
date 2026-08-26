using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public interface ISparePartRepository
{
    Task<SparePart?> GetByIdAsync(string partId, CancellationToken ct = default);
    Task<SparePartStockTransaction?> GetTransactionByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> IsUsageScopeValidAsync(
        string partId,
        string equipmentId,
        string? bomItemId,
        string? workOrderId,
        CancellationToken ct = default);
    Task<IReadOnlyList<SparePart>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SparePart>> GetLowStockAsync(CancellationToken ct = default);
    Task AddAsync(SparePart part, string actorId, CancellationToken ct = default);
    Task UpdateAsync(SparePart part, CancellationToken ct = default);
    Task<bool> PersistAdjustmentAsync(
        SparePartStockTransaction transaction,
        CancellationToken ct = default);
}
