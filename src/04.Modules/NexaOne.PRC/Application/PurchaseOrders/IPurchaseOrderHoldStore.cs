namespace NexaOne.PRC.Application.PurchaseOrders;

internal enum PurchaseOrderHoldOutcome
{
    Applied,
    NotFound,
    NotAllowed,
}

internal interface IPurchaseOrderHoldStore
{
    Task<PurchaseOrderHoldOutcome> TryHoldAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);

    Task<PurchaseOrderHoldOutcome> TryReleaseAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);
}
