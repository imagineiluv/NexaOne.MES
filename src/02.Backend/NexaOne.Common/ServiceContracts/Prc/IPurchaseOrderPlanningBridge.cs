using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Prc;

/// <summary>
/// MRP와 PRC 사이의 소유권 경계입니다. 예정입고 조회와 구매오더 생성 규칙은 PRC가 소유합니다.
/// </summary>
[NexaModuleBridge("Prc", "purchaseOrderPlanningBridge")]
public interface IPurchaseOrderPlanningBridge : INexaModuleBridge
{
    Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(CancellationToken ct = default);

    /// <summary>
    /// <paramref name="request"/>의 PurchaseOrderId를 멱등키로 사용합니다. 같은 내용의 재호출은 기존
    /// 오더를 반환하고, 같은 ID에 다른 내용이 이미 있으면 충돌로 실패합니다.
    /// </summary>
    Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
        MrpPurchaseOrderRequest request,
        CancellationToken ct = default);
}

public sealed record MrpPurchaseReceipt(string ProductId, decimal Quantity, DateTime? IncomingDate);

public sealed record MrpPurchaseOrderRequest(
    string PurchaseOrderId,
    string PlantId,
    string PurchaseOrderName,
    DateTime OrderDate,
    DateTime? IncomingDate,
    decimal Quantity,
    string ProductId,
    string Description,
    string ExecutedBy);

public sealed record PurchaseOrderEnsureResult(string PurchaseOrderId, bool Created);
