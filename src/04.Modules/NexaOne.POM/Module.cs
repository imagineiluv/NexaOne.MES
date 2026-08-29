using Microsoft.Extensions.Configuration;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Mrp;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;
using NexaOne.POM.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Prc;
using NexaOne.ServiceContracts.Qms;
using NexaDB.Data.Abstractions.Interfaces;

namespace NexaOne.POM;

/// <summary>POM 내부 저장소·업무 그래프를 숨기고 생산 공개 인터페이스만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IPomBridge _pomBridge;
    private readonly IPomWorkOrderBridge _workOrderBridge;
    private readonly IWorkScopeBridge _workScopeBridge;
    private readonly ILotDispositionBridge _lotDispositionBridge;
    private readonly IMrpBridge _mrpBridge;
    private readonly IProductionLotDirectory _productionLotDirectory;
    private readonly IOeeProductionDirectory _oeeProductionDirectory;

    public Module(
        EesDataSource dataSource,
        IConfiguration configuration,
        INexaOneEESDbCapability dialect,
        ITrackingMasterGateway trackingMasterGateway,
        IProductionQualityGateway productionQualityGateway,
        IMrpMasterDirectory mrpMasterDirectory,
        IMrpInventoryDirectory mrpInventoryDirectory,
        IPurchaseOrderPlanningBridge purchaseOrderPlanningBridge,
        IEquipmentDirectory equipmentDirectory,
        IEquipmentOutputMasterDirectory? equipmentOutputMasterDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(trackingMasterGateway);
        ArgumentNullException.ThrowIfNull(productionQualityGateway);
        ArgumentNullException.ThrowIfNull(mrpMasterDirectory);
        ArgumentNullException.ThrowIfNull(mrpInventoryDirectory);
        ArgumentNullException.ThrowIfNull(purchaseOrderPlanningBridge);
        ArgumentNullException.ThrowIfNull(equipmentDirectory);

        var plans = new ProductionPlanRepository(dataSource, configuration);
        var orders = new ProductionOrderRepository(dataSource, configuration);
        var workOrders = new PomWorkOrderRepository(dataSource);
        var workScopes = new WorkScopeRepository(dataSource);
        var lots = new LotRepository(dataSource, configuration);
        var lotService = new LotTrackingService(
            lots,
            lots,
            new LotHistoryRepository(dataSource, dialect),
            new LotMixingRelationRepository(dataSource),
            workOrders,
            trackingMasterGateway,
            productionQualityGateway,
            new RoutingPolicyEvaluator());

        _pomBridge = new PomBridge(
            new PomService(plans),
            new ProductionOrderService(orders),
            lotService);
        _workOrderBridge = new PomWorkOrderBridge(
            new PomWorkOrderService(workOrders, orders, lots, productionQualityGateway));
        _workScopeBridge = new WorkScopeBridge(
            new WorkScopeService(workScopes, equipmentOutputMasterDirectory));
        _lotDispositionBridge = new LotDispositionBridge(
            new LotDispositionService(new LotDispositionRepository(dataSource)));
        _mrpBridge = new MrpBridge(new MrpPlanningRepository(
            dataSource,
            new LegacySalesOrderMrpProjection(dataSource),
            mrpMasterDirectory,
            mrpInventoryDirectory,
            purchaseOrderPlanningBridge,
            equipmentDirectory));
        _productionLotDirectory = new ProductionLotDirectory(dataSource);
        _oeeProductionDirectory = new OeeProductionDirectory(dataSource);
    }

    public IPomBridge GetPomBridge() => _pomBridge;
    public IPomWorkOrderBridge GetWorkOrderBridge() => _workOrderBridge;
    public IWorkScopeBridge GetWorkScopeBridge() => _workScopeBridge;
    public ILotDispositionBridge GetLotDispositionBridge() => _lotDispositionBridge;
    public IMrpBridge GetMrpBridge() => _mrpBridge;
    public IProductionLotDirectory GetProductionLotDirectory() => _productionLotDirectory;
    public IOeeProductionDirectory GetOeeProductionDirectory() => _oeeProductionDirectory;
}
