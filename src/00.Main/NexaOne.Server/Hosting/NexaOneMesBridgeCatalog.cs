using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Prc;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Rms;
using NexaOne.ServiceContracts.Shp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server;

/// <summary>공유 계약과 Spring 서비스/Bean 연결을 나타내는 제품 호스트 전용 값입니다.</summary>
internal sealed record NexaModuleBridgeDescriptor(Type ContractType, string Module, string BeanName);

/// <summary>제품 호스트가 선언한 Spring bridge 연결을 조회하는 런타임 내부 인터페이스입니다.</summary>
internal interface INexaModuleBridgeCatalog
{
    IReadOnlyList<NexaModuleBridgeDescriptor> Descriptors { get; }
    bool TryGet(Type contractType, out NexaModuleBridgeDescriptor descriptor);
}

/// <summary>
/// 제품 호스트가 소유하는 Spring 모듈/Bean 조립 명세입니다. 공유 계약 어셈블리는 업무 인터페이스만
/// 정의하고, 어느 Spring 컨텍스트의 어떤 Bean에 연결할지는 이 composition root가 명시합니다.
/// </summary>
internal sealed class NexaOneMesBridgeCatalog : INexaModuleBridgeCatalog
{
    private readonly IReadOnlyDictionary<Type, NexaModuleBridgeDescriptor> _byContract;

    private NexaOneMesBridgeCatalog(IReadOnlyList<NexaModuleBridgeDescriptor> descriptors)
    {
        Descriptors = descriptors;
        _byContract = descriptors.ToDictionary(static descriptor => descriptor.ContractType);
    }

    public IReadOnlyList<NexaModuleBridgeDescriptor> Descriptors { get; }

    public bool TryGet(Type contractType, out NexaModuleBridgeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return _byContract.TryGetValue(contractType, out descriptor!);
    }

    /// <summary>컴파일 타임 계약 형식과 명시적인 Spring 연결만으로 검증된 catalog를 만듭니다.</summary>
    public static NexaOneMesBridgeCatalog Create()
        => Create(
            Bind<IEmsBridge>("Ems", "emsBridge"),
            Bind<IMaintenanceExecutionBridge>("Ems", "maintenanceExecutionBridge"),
            Bind<IMaintenanceScheduleBridge>("Ems", "maintenanceScheduleBridge"),
            Bind<ISparePartBridge>("Ems", "sparePartBridge"),
            Bind<IToolBridge>("Ems", "toolBridge"),
            Bind<IEquipmentAlarmBridge>("Est", "equipmentAlarmBridge"),
            Bind<IEquipmentOutputBridge>("Est", "equipmentOutputBridge"),
            Bind<IEquipmentStateBridge>("Est", "equipmentStateBridge"),
            Bind<IOeeAggregationBridge>("Est", "oeeAggregationBridge"),
            Bind<IUtilityBridge>("Est", "utilityBridge"),
            Bind<IFdcBridge>("Fdc", "fdcBridge"),
            Bind<IFdcRuntimeLease>("Fdc", "fdcRuntimeLease"),
            Bind<IFdcTraceSource>("Fdc", "fdcTraceSource"),
            Bind<IFdcTraceRetentionGuard>("Ivt", "fdcTraceRetentionGuard"),
            Bind<IMaterialBridge>("Ivt", "materialBridge"),
            Bind<IMaterialLotBridge>("Ivt", "materialLotBridge"),
            Bind<IMaterialLotDirectory>("Ivt", "materialLotDirectory"),
            Bind<IMrpInventoryDirectory>("Ivt", "mrpInventoryDirectory"),
            Bind<IEquipmentDirectory>("Mdm", "equipmentDirectory"),
            Bind<IEquipmentOutputMasterDirectory>("Mdm", "equipmentOutputMasterDirectory"),
            Bind<IMdmEquipmentBridge>("Mdm", "mdmEquipmentBridge"),
            Bind<IMdmMasterBridge>("Mdm", "mdmMasterBridge"),
            Bind<IMrpMasterDirectory>("Mdm", "mrpMasterDirectory"),
            Bind<IOeePlanDirectory>("Mdm", "oeePlanDirectory"),
            Bind<IProcessDirectory>("Mdm", "processDirectory"),
            Bind<ITrackingRoutingDirectory>("Mdm", "trackingRoutingDirectory"),
            Bind<IVendorDirectory>("Mdm", "vendorDirectory"),
            Bind<ILotDispositionBridge>("Pom", "lotDispositionBridge"),
            Bind<IMrpBridge>("Pom", "mrpBridge"),
            Bind<IOeeProductionDirectory>("Pom", "oeeProductionDirectory"),
            Bind<IPomBridge>("Pom", "pomBridge"),
            Bind<IPomWorkOrderBridge>("Pom", "pomWorkOrderBridge"),
            Bind<IProductionLotDirectory>("Pom", "productionLotDirectory"),
            Bind<IPurchaseOrderPlanningBridge>("Prc", "purchaseOrderPlanningBridge"),
            Bind<IQmsBridge>("Qms", "qmsBridge"),
            Bind<IProductionQualityGateway>("Qms", "qmsProductionQualityGateway"),
            Bind<ITrackingDefectDirectory>("Qms", "trackingDefectDirectory"),
            Bind<IRecipeApprovalBridge>("Rms", "rmsRecipeBridge"),
            Bind<IRecipeExecutionBridge>("Rms", "rmsRecipeExecutionBridge"),
            Bind<ITrackingRecipeDirectory>("Rms", "trackingRecipeDirectory"),
            Bind<IShipmentBridge>("Shp", "shipmentBridge"),
            Bind<IDeployBridge>("Sys", "deployBridge"),
            Bind<IMaintenanceIdentityDirectory>("Sys", "maintenanceIdentityDirectory"),
            Bind<ISysBridge>("Sys", "sysBridge"),
            Bind<IUserDirectory>("Sys", "userDirectory"));

    internal static NexaOneMesBridgeCatalog Create(
        params NexaModuleBridgeDescriptor[] descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var contracts = new HashSet<Type>();
        var bindings = new HashSet<(string Module, string BeanName)>();
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!descriptor.ContractType.IsInterface
                || !typeof(INexaModuleBridge).IsAssignableFrom(descriptor.ContractType))
            {
                throw new InvalidOperationException(
                    $"Module bridge contract must be an {nameof(INexaModuleBridge)} interface: "
                    + $"'{descriptor.ContractType.FullName}'.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Module)
                || string.IsNullOrWhiteSpace(descriptor.BeanName))
            {
                throw new InvalidOperationException(
                    $"Module bridge binding for '{descriptor.ContractType.FullName}' is blank.");
            }

            if (!contracts.Add(descriptor.ContractType))
                throw new InvalidOperationException(
                    $"Module bridge contract '{descriptor.ContractType.FullName}' is duplicated.");
            if (!bindings.Add((descriptor.Module.Trim(), descriptor.BeanName.Trim())))
                throw new InvalidOperationException(
                    $"Module bridge binding '{descriptor.Module}/{descriptor.BeanName}' is duplicated.");
        }

        var ordered = descriptors
            .Select(static descriptor => descriptor with
            {
                Module = descriptor.Module.Trim(),
                BeanName = descriptor.BeanName.Trim(),
            })
            .OrderBy(static descriptor => descriptor.Module, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.BeanName, StringComparer.Ordinal)
            .ThenBy(
                static descriptor => descriptor.ContractType.FullName ?? descriptor.ContractType.Name,
                StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        return new NexaOneMesBridgeCatalog(ordered);
    }

    private static NexaModuleBridgeDescriptor Bind<TContract>(string module, string beanName)
        where TContract : class, INexaModuleBridge
        => new(typeof(TContract), module, beanName);
}
