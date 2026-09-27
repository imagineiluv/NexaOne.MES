namespace NexaOne.PRC.Application.PurchaseOrders;

internal enum PurchaseOrderCommandOutcome
{
    Applied,
    NotFound,
    NotAllowed,
}

internal interface IPurchaseOrderCommandStore
{
    Task<PurchaseOrderCommandOutcome> TryOrderAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);

    Task<PurchaseOrderCommandOutcome> TryCloseAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);
}
