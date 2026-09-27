using NexaOne.Common;

namespace NexaOne.ServiceContracts.Prc;

/// <summary>PRC owns the guarded Draft→Ordered and Incoming→Closed transitions.</summary>
public interface IPurchaseOrderCommandBridge : INexaModuleBridge
{
    Task<Result<PurchaseOrderCommandState>> OrderAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default);

    Task<Result<PurchaseOrderCommandState>> CloseAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default);
}

public sealed record PurchaseOrderCommandState(string PurchaseOrderId, string Status);
