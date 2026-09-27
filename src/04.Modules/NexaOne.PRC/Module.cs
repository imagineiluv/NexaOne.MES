using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Application.PurchaseOrders;
using NexaOne.PRC.Infrastructure;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC;

/// <summary>PRC의 단일 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IPurchaseOrderPlanningBridge _purchaseOrderPlanningBridge;
    private readonly IPurchaseOrderItemBridge _purchaseOrderItemBridge;
    private readonly ISqliteSchemaContribution _purchaseItemSqliteSchemaContribution;

    public Module(EesDataSource dataSource, IBusinessMasterDirectory masterDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(masterDirectory);
        var store = new PurchaseOrderPlanningRepository(dataSource);
        var service = new PurchaseOrderPlanningService(store);
        _purchaseOrderPlanningBridge = new PurchaseOrderPlanningBridge(service);
        _purchaseOrderItemBridge = new PurchaseOrderItemService(
            new PurchaseOrderItemRepository(dataSource, masterDirectory));
        _purchaseItemSqliteSchemaContribution = new PurchaseItemSqliteSchemaContribution();
    }

    public IPurchaseOrderPlanningBridge GetPurchaseOrderPlanningBridge() => _purchaseOrderPlanningBridge;
    public IPurchaseOrderItemBridge GetPurchaseOrderItemBridge() => _purchaseOrderItemBridge;
    public ISqliteSchemaContribution GetPurchaseItemSqliteSchemaContribution() =>
        _purchaseItemSqliteSchemaContribution;
}
