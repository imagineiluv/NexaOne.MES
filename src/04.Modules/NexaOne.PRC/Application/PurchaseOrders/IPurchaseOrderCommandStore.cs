namespace NexaOne.PRC.Application.PurchaseOrders;

internal enum PurchaseOrderCommandOutcome
{
    Applied,
    NotFound,
    NotAllowed,
}

internal enum PurchaseOrderDraftOutcome
{
    Saved,
    NotEditable,
    IdentityConflict,
}

internal sealed record PurchaseOrderHeaderDraft(
    string PurchaseOrderId,
    string PlantId,
    string? PurchaseOrderName,
    string? VendorId,
    decimal OrderQuantity,
    string ActorId);

internal interface IPurchaseOrderCommandStore
{
    Task<PurchaseOrderDraftOutcome> TrySaveDraftAsync(
        PurchaseOrderHeaderDraft draft, CancellationToken ct);

    Task<PurchaseOrderCommandOutcome> TryDeleteDraftAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);

    Task<PurchaseOrderCommandOutcome> TryOrderAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);

    Task<PurchaseOrderCommandOutcome> TryCloseAsync(
        string purchaseOrderId, string actorId, CancellationToken ct);
}
