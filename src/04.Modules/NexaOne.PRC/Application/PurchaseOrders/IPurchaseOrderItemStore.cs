using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

internal interface IPurchaseOrderItemStore
{
    Task<PurchaseItemWriteResult> TrySaveAsync(
        string purchaseOrderId, string productId, decimal quantity, string actorId,
        CancellationToken ct);

    Task<PurchaseItemWriteResult> TryDeleteAsync(
        string purchaseOrderId, string productId, string actorId, CancellationToken ct);

    Task<IReadOnlyList<PurchaseOrderItem>?> ListAsync(
        string purchaseOrderId, CancellationToken ct);
}

internal enum PurchaseItemWriteOutcome
{
    Applied,
    OrderNotFound,
    ItemNotFound,
    NotEditable,
    InvalidProduct,
    QuantityOverflow,
}

internal sealed record PurchaseItemWriteResult(
    PurchaseItemWriteOutcome Outcome, decimal OrderQuantity = 0);
