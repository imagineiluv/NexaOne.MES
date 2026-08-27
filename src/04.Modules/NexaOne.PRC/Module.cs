using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Infrastructure;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC;

/// <summary>PRC의 단일 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IPurchaseOrderPlanningBridge _purchaseOrderPlanningBridge;

    public Module(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _purchaseOrderPlanningBridge = new PurchaseOrderPlanningBridge(dataSource);
    }

    public IPurchaseOrderPlanningBridge GetPurchaseOrderPlanningBridge() => _purchaseOrderPlanningBridge;
}
