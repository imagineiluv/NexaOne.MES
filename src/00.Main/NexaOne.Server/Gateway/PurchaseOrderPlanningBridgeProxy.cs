using NexaFramework;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.Server.Gateway;

/// <summary>PRC 예정입고·구매오더 command를 POM 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class PurchaseOrderPlanningBridgeProxy : IPurchaseOrderPlanningBridge
{
    public Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(CancellationToken ct = default)
        => Resolve().GetScheduledReceiptsAsync(ct);

    public Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
        MrpPurchaseOrderRequest request,
        CancellationToken ct = default)
        => Resolve().EnsureMrpPurchaseOrderAsync(request, ct);

    private static IPurchaseOrderPlanningBridge Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Prc", "purchaseOrderPlanningBridge");
        return bean as IPurchaseOrderPlanningBridge
            ?? throw ModuleProxy.TypeMismatch<IPurchaseOrderPlanningBridge>(
                "Prc", "purchaseOrderPlanningBridge", bean);
    }
}
