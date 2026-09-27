using NexaOne.Common;

namespace NexaOne.ServiceContracts.Prc;

/// <summary>PRC owns purchase-order hold and release independently of line editing.</summary>
public interface IPurchaseOrderHoldBridge : INexaModuleBridge
{
    Task<Result<PurchaseOrderHoldState>> HoldAsync(string? purchaseOrderId, string? actorId,
        CancellationToken ct = default);

    Task<Result<PurchaseOrderHoldState>> ReleaseAsync(string? purchaseOrderId, string? actorId,
        CancellationToken ct = default);
}

public sealed record PurchaseOrderHoldState(string PurchaseOrderId, bool IsHeld);
