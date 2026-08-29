using Microsoft.Extensions.Configuration;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.QMS;

/// <summary>QMS 저장소·업무 구현을 숨기고 품질 공개 인터페이스만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IQmsBridge _qmsBridge;
    private readonly IProductionQualityGateway _productionQualityGateway;
    private readonly ITrackingDefectDirectory _trackingDefectDirectory;

    public Module(
        EesDataSource dataSource,
        IConfiguration configuration,
        IProductionLotDirectory productionLotDirectory,
        IMaterialLotDirectory materialLotDirectory,
        IEquipmentDirectory equipmentDirectory,
        IProcessDirectory processDirectory,
        IUserDirectory userDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(productionLotDirectory);
        ArgumentNullException.ThrowIfNull(materialLotDirectory);
        ArgumentNullException.ThrowIfNull(equipmentDirectory);
        ArgumentNullException.ThrowIfNull(processDirectory);
        ArgumentNullException.ThrowIfNull(userDirectory);

        var defectClasses = new DefectClassRepository(dataSource);
        var references = new QmsReferenceRepository(
            productionLotDirectory,
            materialLotDirectory,
            equipmentDirectory,
            processDirectory,
            userDirectory);
        var service = new QmsService(
            new DefectRepository(dataSource, configuration),
            defectClasses,
            new InspectionSpecRepository(dataSource),
            new InspectionResultRepository(dataSource, productionLotDirectory, materialLotDirectory),
            new SpcParamRepository(dataSource),
            references);
        _qmsBridge = new QmsBridge(
            service,
            new AdvancedQualityService(new AdvancedQualityRepository(dataSource)),
            new AiInspectionService(new AiInspectionRepository(dataSource)));
        _productionQualityGateway = new ProductionQualityGateService(
            new ProductionQualityGateEvidenceRepository(dataSource));
        _trackingDefectDirectory = new TrackingDefectDirectory(dataSource);
    }

    public IQmsBridge GetQmsBridge() => _qmsBridge;
    public IProductionQualityGateway GetProductionQualityGateway() => _productionQualityGateway;
    public ITrackingDefectDirectory GetTrackingDefectDirectory() => _trackingDefectDirectory;
}
