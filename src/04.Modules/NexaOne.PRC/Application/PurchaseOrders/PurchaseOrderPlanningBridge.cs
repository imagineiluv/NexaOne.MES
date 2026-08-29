using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

/// <summary>공개 모듈 계약을 PRC 구매오더 계획 유스케이스에 연결하는 얇은 어댑터입니다.</summary>
internal sealed class PurchaseOrderPlanningBridge : IPurchaseOrderPlanningBridge
{
    private readonly PurchaseOrderPlanningService _service;

    public PurchaseOrderPlanningBridge(PurchaseOrderPlanningService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(
        CancellationToken ct = default)
        => _service.GetScheduledReceiptsAsync(ct);

    public Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
        MrpPurchaseOrderRequest request,
        CancellationToken ct = default)
        => _service.EnsureMrpPurchaseOrderAsync(request, ct);
}
