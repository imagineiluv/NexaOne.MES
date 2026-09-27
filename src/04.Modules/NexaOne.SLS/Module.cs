using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Shp;
using NexaOne.ServiceContracts.Sls;
using NexaOne.SLS.Application;
using NexaOne.SLS.Infrastructure;

namespace NexaOne.SLS;

/// <summary>SLS의 단일 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IMrpDemandDirectory _mrpDemandDirectory;
    private readonly ISalesRequestBridge _salesRequestBridge;
    private readonly ISalesOrderDeliveryBridge _salesOrderDeliveryBridge;

    public Module(EesDataSource dataSource, IBusinessMasterDirectory masterDirectory,
        ISalesOrderShipmentIntake shipmentIntake)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(masterDirectory);
        ArgumentNullException.ThrowIfNull(shipmentIntake);
        _mrpDemandDirectory = new MrpDemandDirectory(dataSource);
        _salesRequestBridge = new SalesRequestService(new SalesRequestRepository(dataSource, masterDirectory));
        _salesOrderDeliveryBridge = new SalesOrderDeliveryService(
            new SalesOrderDeliveryRepository(dataSource, masterDirectory, shipmentIntake));
    }

    public IMrpDemandDirectory GetMrpDemandDirectory() => _mrpDemandDirectory;
    public ISalesRequestBridge GetSalesRequestBridge() => _salesRequestBridge;
    public ISalesOrderDeliveryBridge GetSalesOrderDeliveryBridge() => _salesOrderDeliveryBridge;
}
