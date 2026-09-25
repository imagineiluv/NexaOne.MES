using System.Data.Common;
using NexaFramework.Service;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>Read-only inventory-owned projection used to validate and resolve billable stock issues
/// inside the caller's existing serializable transaction.</summary>
public interface IStockBillingDirectory : INexaModuleBridge
{
    Task<StockBillingOccurrence?> GetMovementInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, Guid movementId, CancellationToken ct = default);
    Task<IReadOnlyList<StockBillingOccurrence>> FindMovementsInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, IReadOnlyList<Guid> movementIds, CancellationToken ct = default);
}

/// <summary>Minimal immutable inventory facts required by product billing.</summary>
public sealed record StockBillingOccurrence(Guid MovementId, Guid ProductId, Guid VariantId,
    DateOnly OccurredOn, decimal Quantity, bool BillableIssue, bool Reversed);
