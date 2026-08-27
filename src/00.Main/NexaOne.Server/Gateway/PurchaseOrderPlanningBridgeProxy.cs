using NexaOne.ServiceContracts.Prc;

namespace NexaOne.Server.Gateway;

/// <summary>PRC 예정입고·구매오더 command를 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class PurchaseOrderPlanningBridgeProxy : IPurchaseOrderPlanningBridge
{
    private readonly ModuleBeanResolver _resolver;

    public PurchaseOrderPlanningBridgeProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(CancellationToken ct = default)
        => Resolve().GetScheduledReceiptsAsync(ct);

    public Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
        MrpPurchaseOrderRequest request,
        CancellationToken ct = default)
        => Resolve().EnsureMrpPurchaseOrderAsync(request, ct);

    private IPurchaseOrderPlanningBridge Resolve() =>
        _resolver.Resolve<IPurchaseOrderPlanningBridge>("Prc", "purchaseOrderPlanningBridge");
}
