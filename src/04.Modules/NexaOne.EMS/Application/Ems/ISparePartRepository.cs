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
        string equipmentClassId,
        string? bomItemId,
        string? workOrderId,
        CancellationToken ct = default);
    Task<IReadOnlyList<SparePart>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SparePart>> GetLowStockAsync(CancellationToken ct = default);
    /// <summary>
    /// Atomically creates the spare-part master and its Opening balance ledger. Returns false for
    /// an identity/idempotency winner so the caller can reload and resolve the replay.
    /// </summary>
    Task<bool> TryAddWithOpeningBalanceAsync(
        SparePart part,
        SparePartStockTransaction openingBalance,
        CancellationToken ct = default);
    Task<bool> PersistAdjustmentAsync(
        SparePartStockTransaction transaction,
        string? equipmentClassId,
        CancellationToken ct = default);
}
