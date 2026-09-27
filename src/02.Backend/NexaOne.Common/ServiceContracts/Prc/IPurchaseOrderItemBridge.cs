using NexaOne.Common;

namespace NexaOne.ServiceContracts.Prc;

/// <summary>PRC owns Draft purchase lines and the header quantity derived from them.</summary>
public interface IPurchaseOrderItemBridge : INexaModuleBridge
{
    Task<Result<PurchaseOrderItemTotal>> SaveDraftItemAsync(
        string? purchaseOrderId, string? productId, decimal quantity, string? actorId,
        CancellationToken ct = default);

    Task<Result<PurchaseOrderItemTotal>> DeleteDraftItemAsync(
        string? purchaseOrderId, string? productId, string? actorId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<PurchaseOrderItem>>> ListAsync(
        string? purchaseOrderId, CancellationToken ct = default);
}

public sealed record PurchaseOrderItemTotal(string PurchaseOrderId, decimal OrderQuantity);

public sealed record PurchaseOrderItem(string ProductId, decimal OrderQuantity, decimal IncomingQuantity);
