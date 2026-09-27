using NexaOne.Common;

namespace NexaOne.ServiceContracts.Prc;

/// <summary>PRC owns purchase-order Draft writes and guarded status transitions.</summary>
public interface IPurchaseOrderCommandBridge : INexaModuleBridge
{
    Task<Result<PurchaseOrderCommandState>> SaveDraftAsync(
        PurchaseOrderDraftCommand command, CancellationToken ct = default);

    Task<Result> DeleteDraftAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default);

    Task<Result<PurchaseOrderCommandState>> OrderAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default);

    Task<Result<PurchaseOrderCommandState>> CloseAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default);
}

public sealed record PurchaseOrderCommandState(string PurchaseOrderId, string Status);

public sealed record PurchaseOrderDraftCommand(
    string? PurchaseOrderId,
    string? PlantId,
    string? PurchaseOrderName,
    string? VendorId,
    decimal OrderQuantity,
    string? ActorId);
