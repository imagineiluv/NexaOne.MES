using Microsoft.Extensions.Configuration;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Shp;
using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Infrastructure;

namespace NexaOne.SHP;

/// <summary>SHP 내부 저장소 그래프를 숨기고 출하 bridge만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IShipmentBridge _shipmentBridge;
    private readonly ISalesOrderShipmentIntake _salesOrderShipmentIntake;
    private readonly ISalesOrderShipmentEvidence _salesOrderShipmentEvidence;

    public Module(EesDataSource dataSource, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        _shipmentBridge = new ShipmentBridge(new ShpService(
            new DeliveryOrderRepository(dataSource, configuration),
            new DeliveryItemRepository(dataSource),
            new ShipmentHistoryRepository(dataSource)));
        _salesOrderShipmentIntake = new SalesOrderShipmentIntake();
        _salesOrderShipmentEvidence = new SalesOrderShipmentEvidence();
    }

    public IShipmentBridge GetShipmentBridge() => _shipmentBridge;
    public ISalesOrderShipmentIntake GetSalesOrderShipmentIntake() => _salesOrderShipmentIntake;
    public ISalesOrderShipmentEvidence GetSalesOrderShipmentEvidence() => _salesOrderShipmentEvidence;
}
