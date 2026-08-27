using System.Reflection;
using System.Reflection.Emit;
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

namespace NexaOne.UnitTests.Common;

/// <summary>
/// 공유 Bridge 계약의 자동 발견 결과와 잘못된 marker/attribute 구성이 시작 시 차단되는지 검증한다.
/// 잘못된 계약은 운영·테스트 어셈블리를 오염시키지 않도록 테스트마다 별도 동적 어셈블리에 생성한다.
/// </summary>
public sealed class NexaModuleBridgeCatalogTests
{
    private static readonly ConstructorInfo BridgeAttributeConstructor =
        typeof(NexaModuleBridgeAttribute).GetConstructor(new[] { typeof(string), typeof(string) })
        ?? throw new InvalidOperationException("NexaModuleBridgeAttribute 생성자를 찾을 수 없습니다.");

    private static readonly NexaModuleBridgeDescriptor[] ExpectedProductionDescriptors =
    {
        new(typeof(IEmsBridge), "Ems", "emsBridge"),
        new(typeof(IMaintenanceExecutionBridge), "Ems", "maintenanceExecutionBridge"),
        new(typeof(IMaintenanceScheduleBridge), "Ems", "maintenanceScheduleBridge"),
        new(typeof(ISparePartBridge), "Ems", "sparePartBridge"),
        new(typeof(IToolBridge), "Ems", "toolBridge"),
        new(typeof(IEquipmentAlarmBridge), "Est", "equipmentAlarmBridge"),
        new(typeof(IEquipmentOutputBridge), "Est", "equipmentOutputBridge"),
        new(typeof(IEquipmentStateBridge), "Est", "equipmentStateBridge"),
        new(typeof(IOeeAggregationBridge), "Est", "oeeAggregationBridge"),
        new(typeof(IUtilityBridge), "Est", "utilityBridge"),
        new(typeof(IFdcBridge), "Fdc", "fdcBridge"),
        new(typeof(IFdcTraceSource), "Fdc", "fdcTraceSource"),
        new(typeof(IMaterialBridge), "Ivt", "materialBridge"),
        new(typeof(IMaterialLotBridge), "Ivt", "materialLotBridge"),
        new(typeof(IMaterialLotDirectory), "Ivt", "materialLotDirectory"),
        new(typeof(IMrpInventoryDirectory), "Ivt", "mrpInventoryDirectory"),
        new(typeof(IEquipmentDirectory), "Mdm", "equipmentDirectory"),
        new(typeof(IEquipmentOutputMasterDirectory), "Mdm", "equipmentOutputMasterDirectory"),
        new(typeof(IMdmEquipmentBridge), "Mdm", "mdmEquipmentBridge"),
        new(typeof(IMdmMasterBridge), "Mdm", "mdmMasterBridge"),
        new(typeof(IMrpMasterDirectory), "Mdm", "mrpMasterDirectory"),
        new(typeof(IProcessDirectory), "Mdm", "processDirectory"),
        new(typeof(IVendorDirectory), "Mdm", "vendorDirectory"),
        new(typeof(ILotDispositionBridge), "Pom", "lotDispositionBridge"),
        new(typeof(IMrpBridge), "Pom", "mrpBridge"),
        new(typeof(IPomBridge), "Pom", "pomBridge"),
        new(typeof(IPomWorkOrderBridge), "Pom", "pomWorkOrderBridge"),
        new(typeof(IProductionLotDirectory), "Pom", "productionLotDirectory"),
        new(typeof(IPurchaseOrderPlanningBridge), "Prc", "purchaseOrderPlanningBridge"),
        new(typeof(IQmsBridge), "Qms", "qmsBridge"),
        new(typeof(IProductionQualityGateway), "Qms", "qmsProductionQualityGateway"),
        new(typeof(IRecipeApprovalBridge), "Rms", "rmsRecipeBridge"),
        new(typeof(IRecipeExecutionBridge), "Rms", "rmsRecipeExecutionBridge"),
        new(typeof(IShipmentBridge), "Shp", "shipmentBridge"),
        new(typeof(IDeployBridge), "Sys", "deployBridge"),
        new(typeof(IMaintenanceIdentityDirectory), "Sys", "maintenanceIdentityDirectory"),
        new(typeof(ISysBridge), "Sys", "sysBridge"),
        new(typeof(IUserDirectory), "Sys", "userDirectory"),
    };

    [Fact]
    public void Discover_finds_all_production_contracts_in_deterministic_order()
    {
        var first = NexaModuleBridgeCatalog.Discover(typeof(INexaModuleBridge).Assembly);
        var second = NexaModuleBridgeCatalog.Discover(typeof(INexaModuleBridge).Assembly);

        first.Descriptors.Should().HaveCount(38);
        first.Descriptors.Should().Equal(ExpectedProductionDescriptors);
        second.Descriptors.Should().Equal(first.Descriptors);
        first.Descriptors.Should().OnlyContain(descriptor =>
            descriptor.ContractType.IsInterface
            && typeof(INexaModuleBridge).IsAssignableFrom(descriptor.ContractType));
    }

    [Fact]
    public void TryGet_returns_descriptor_by_contract()
    {
        INexaModuleBridgeCatalog catalog =
            NexaModuleBridgeCatalog.Discover(typeof(INexaModuleBridge).Assembly);

        catalog.TryGet(typeof(IPomWorkOrderBridge), out var descriptor).Should().BeTrue();
        descriptor.Should().Be(
            new NexaModuleBridgeDescriptor(typeof(IPomWorkOrderBridge), "Pom", "pomWorkOrderBridge"));
        catalog.TryGet(typeof(IDisposable), out _).Should().BeFalse();
    }

    [Fact]
    public void Discover_keeps_loadable_types_when_an_assembly_partially_fails()
    {
        var emittedAssembly = EmitAssembly(module =>
            EmitBridgeType(module, "PartialBridge", isInterface: true, hasMarker: true, ("Partial", "partialBridge")));
        var bridgeType = emittedAssembly.GetType("DynamicContracts.PartialBridge", throwOnError: true)!;
        var partialAssembly = new PartialLoadAssembly(bridgeType);

        var catalog = NexaModuleBridgeCatalog.Discover(partialAssembly);

        catalog.Descriptors.Should().Equal(
            new NexaModuleBridgeDescriptor(bridgeType, "Partial", "partialBridge"));
    }

    [Theory]
    [InlineData(true, false, "MarkerOnly")]
    [InlineData(false, true, "AttributeOnly")]
    public void Discover_rejects_marker_and_attribute_mismatch(
        bool hasMarker,
        bool hasAttribute,
        string typeName)
    {
        var attributes = hasAttribute
            ? new[] { (Module: "Mismatch", BeanName: "mismatchBridge") }
            : Array.Empty<(string Module, string BeanName)>();
        var assembly = EmitAssembly(module =>
            EmitBridgeType(module, typeName, isInterface: true, hasMarker, attributes));

        var act = () => NexaModuleBridgeCatalog.Discover(assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*DynamicContracts.{typeName}*");
    }

    [Fact]
    public void Discover_rejects_a_concrete_marker_implementation()
    {
        var assembly = EmitAssembly(module =>
            EmitBridgeType(module, "ConcreteBridge", isInterface: false, hasMarker: true));

        var act = () => NexaModuleBridgeCatalog.Discover(assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DynamicContracts.ConcreteBridge*");
    }

    [Theory]
    [InlineData(" ", "bridge")]
    [InlineData("Module", "\t")]
    public void Discover_rejects_blank_binding_metadata(string moduleName, string beanName)
    {
        var assembly = EmitAssembly(module =>
            EmitBridgeType(module, "BlankMetadataBridge", isInterface: true, hasMarker: true, (moduleName, beanName)));

        var act = () => NexaModuleBridgeCatalog.Discover(assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DynamicContracts.BlankMetadataBridge*");
    }

    [Fact]
    public void Discover_rejects_duplicate_attributes()
    {
        var assembly = EmitAssembly(module =>
            EmitBridgeType(
                module,
                "DuplicateAttributeBridge",
                isInterface: true,
                hasMarker: true,
                ("Duplicate", "firstBridge"),
                ("Duplicate", "secondBridge")));

        var act = () => NexaModuleBridgeCatalog.Discover(assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DynamicContracts.DuplicateAttributeBridge*");
    }

    [Fact]
    public void Discover_rejects_duplicate_module_and_bean_binding()
    {
        var assembly = EmitAssembly(
            module => EmitBridgeType(module, "FirstBridge", isInterface: true, hasMarker: true, ("Shared", "sharedBridge")),
            module => EmitBridgeType(module, "SecondBridge", isInterface: true, hasMarker: true, ("Shared", "sharedBridge")));

        var act = () => NexaModuleBridgeCatalog.Discover(assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Shared/sharedBridge*");
    }

    [Fact]
    public void Discover_rejects_a_contract_discovered_twice()
    {
        var assembly = EmitAssembly(module =>
            EmitBridgeType(module, "RepeatedBridge", isInterface: true, hasMarker: true, ("Repeat", "repeatedBridge")));

        var act = () => NexaModuleBridgeCatalog.Discover(assembly, assembly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DynamicContracts.RepeatedBridge*");
    }

    /// <summary>각 실패 시나리오가 서로 영향을 주지 않도록 고유한 실행 전용 어셈블리를 만든다.</summary>
    private static Assembly EmitAssembly(params Action<ModuleBuilder>[] typeDefinitions)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"NexaModuleBridgeCatalogTests_{suffix}"),
            AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule($"DynamicContracts_{suffix}");

        foreach (var defineType in typeDefinitions)
            defineType(module);

        return assembly;
    }

    /// <summary>요청한 marker와 attribute 조합을 가진 동적 계약 인터페이스 또는 구현 클래스를 만든다.</summary>
    private static Type EmitBridgeType(
        ModuleBuilder module,
        string typeName,
        bool isInterface,
        bool hasMarker,
        params (string Module, string BeanName)[] attributes)
    {
        var typeAttributes = TypeAttributes.Public
                             | (isInterface
                                 ? TypeAttributes.Interface | TypeAttributes.Abstract
                                 : TypeAttributes.Class | TypeAttributes.Sealed);
        var type = module.DefineType($"DynamicContracts.{typeName}", typeAttributes);

        if (hasMarker)
            type.AddInterfaceImplementation(typeof(INexaModuleBridge));

        foreach (var (moduleName, beanName) in attributes)
        {
            type.SetCustomAttribute(new CustomAttributeBuilder(
                BridgeAttributeConstructor,
                new object[] { moduleName, beanName }));
        }

        return type.CreateType()!;
    }

    /// <summary>일부 형식만 반환하는 <see cref="ReflectionTypeLoadException"/> 경로를 재현한다.</summary>
    private sealed class PartialLoadAssembly(Type loadableType) : Assembly
    {
        private readonly string _fullName = $"PartialLoadAssembly_{Guid.NewGuid():N}";

        public override string FullName => _fullName;

        public override Type[] GetTypes() =>
            throw new ReflectionTypeLoadException(
                new Type?[] { loadableType, null },
                new Exception?[] { null, new TypeLoadException("의도적으로 로드하지 못한 테스트 형식입니다.") });
    }
}
