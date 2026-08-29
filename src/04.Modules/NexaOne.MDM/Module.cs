using Microsoft.Extensions.Configuration;
using NexaOne.Common.Caching;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM;

/// <summary>
/// MDM의 저장소·업무 구현을 숨기고 외부에 공개할 bridge와 owner directory만 노출하는 조립 진입점입니다.
/// </summary>
public sealed class Module
{
    private readonly IMdmEquipmentBridge _equipmentBridge;
    private readonly IMdmMasterBridge _masterBridge;
    private readonly IEquipmentDirectory _equipmentDirectory;
    private readonly ITrackingRoutingDirectory _trackingRoutingDirectory;
    private readonly IOeePlanDirectory _oeePlanDirectory;
    private readonly IEquipmentOutputMasterDirectory _equipmentOutputMasterDirectory;
    private readonly IVendorDirectory _vendorDirectory;
    private readonly IProcessDirectory _processDirectory;
    private readonly IMrpMasterDirectory _mrpMasterDirectory;

    public Module(EesDataSource dataSource, IConfiguration configuration, ICacheService cache)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cache);

        var equipmentService = new EquipmentService(
            new EquipmentRepository(dataSource, configuration));
        var masterService = new MdmMasterService(
            new PlantRepository(dataSource),
            new AreaRepository(dataSource),
            new ProductRepository(dataSource),
            new CodeRepository(dataSource),
            cache);

        _equipmentBridge = new MdmEquipmentBridge(equipmentService);
        _masterBridge = new MdmMasterBridge(masterService);
        _equipmentDirectory = new EquipmentDirectory(dataSource);
        _trackingRoutingDirectory = new TrackingRoutingDirectory(dataSource);
        _oeePlanDirectory = new OeePlanDirectory(dataSource);
        _equipmentOutputMasterDirectory = new EquipmentOutputMasterDirectory(dataSource);
        _vendorDirectory = new VendorDirectory(dataSource);
        _processDirectory = new ProcessDirectory(dataSource);
        _mrpMasterDirectory = new MrpMasterDirectory(dataSource);
    }

    public IMdmEquipmentBridge GetEquipmentBridge() => _equipmentBridge;
    public IMdmMasterBridge GetMasterBridge() => _masterBridge;
    public IEquipmentDirectory GetEquipmentDirectory() => _equipmentDirectory;
    public ITrackingRoutingDirectory GetTrackingRoutingDirectory() => _trackingRoutingDirectory;
    public IOeePlanDirectory GetOeePlanDirectory() => _oeePlanDirectory;
    public IEquipmentOutputMasterDirectory GetEquipmentOutputMasterDirectory() => _equipmentOutputMasterDirectory;
    public IVendorDirectory GetVendorDirectory() => _vendorDirectory;
    public IProcessDirectory GetProcessDirectory() => _processDirectory;
    public IMrpMasterDirectory GetMrpMasterDirectory() => _mrpMasterDirectory;
}
