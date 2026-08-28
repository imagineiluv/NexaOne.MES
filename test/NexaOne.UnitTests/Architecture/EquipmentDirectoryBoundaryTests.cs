namespace NexaOne.UnitTests.Architecture;

/// <summary>
/// Tool/RMS가 MDM 물리 스키마를 직접 읽지 않고 MDM IEquipmentDirectory seam을 사용하도록 고정한다.
/// 각 저장소는 자기 모듈의 동시성·멱등성 guard만 소유한다.
/// </summary>
public sealed class EquipmentDirectoryBoundaryTests
{
    [Theory]
    [InlineData("NexaOne.EMS", "Infrastructure", "ToolRepository.cs")]
    [InlineData("NexaOne.EMS", "Infrastructure", "MaintenanceExecutionRepository.cs")]
    [InlineData("NexaOne.EMS", "Infrastructure", "SparePartRepository.cs")]
    [InlineData("NexaOne.EMS", "Infrastructure", "SparePartManagementRepository.cs")]
    [InlineData("NexaOne.RMS", "Infrastructure", "RecipeExecutionRepository.cs")]
    public void Equipment_scoped_repositories_contain_no_foreign_MDM_table(
        string module,
        string layer,
        string fileName)
    {
        var path = RepositorySource.GetFile("src", "04.Modules", module, layer, fileName);
        var source = File.ReadAllText(path);

        source.Should().NotContain(
            "MDM_",
            "cross-module equipment validation belongs to the MDM IEquipmentDirectory adapter");
    }

    [Fact]
    public void Ems_infrastructure_contains_no_foreign_master_SQL()
    {
        var infrastructure = RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.EMS", "Infrastructure", "ToolRepository.cs");
        var directory = Path.GetDirectoryName(infrastructure)!;
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(directory, "*.cs").Select(File.ReadAllText));

        source.Should().NotContain("MDM_");
        source.Should().NotContain("SYS_");
    }

    [Fact]
    public void Tool_repository_interface_contains_no_equipment_master_lookup()
    {
        var path = RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.EMS", "Application", "Tools", "IToolRepository.cs");
        var source = File.ReadAllText(path);

        source.Should().NotContain("EquipmentExistsAsync");
        source.Should().NotContain("GetEquipmentClassIdAsync");
        source.Should().NotContain("EquipmentClassExistsAsync");
    }

    [Theory]
    [InlineData("MaintenanceExecution", "IMaintenanceExecutionRepository.cs")]
    [InlineData("SpareParts", "ISparePartManagementRepository.cs")]
    public void Ems_repository_interfaces_contain_no_foreign_master_directory_lookup(
        string feature,
        string fileName)
    {
        var path = RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.EMS", "Application", feature, fileName);
        var source = File.ReadAllText(path);

        source.Should().NotContain("VendorExistsAsync");
        source.Should().NotContain("EquipmentExistsAsync");
        source.Should().NotContain("EquipmentClassExistsAsync");
        source.Should().NotContain("GetActiveWorkerIdAsync");
    }

    [Theory]
    [InlineData("EquipmentDirectoryProxy.cs", "Mdm", "equipmentDirectory")]
    [InlineData("EquipmentOutputMasterDirectoryProxy.cs", "Mdm", "equipmentOutputMasterDirectory")]
    [InlineData("VendorDirectoryProxy.cs", "Mdm", "vendorDirectory")]
    [InlineData("MaintenanceIdentityDirectoryProxy.cs", "Sys", "maintenanceIdentityDirectory")]
    public void Host_directory_adapters_are_thin_sibling_context_proxies(
        string fileName,
        string module,
        string beanName)
    {
        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "Gateway", fileName);
        var source = File.ReadAllText(path);

        source.Should().Contain("ModuleBeanResolver");
        source.Should().Contain("_resolver.Resolve<");
        source.Should().Contain($"\"{module}\"");
        source.Should().Contain($"\"{beanName}\"");
        source.Should().NotContain("ApplicationServer.GetInstance");
        source.Should().NotContain("GetBean(");
        source.Should().NotContain("QueryRepository");
        source.Should().NotContain("EesDataSource");
        source.Should().NotContain("SELECT ");
        source.Should().NotContain("FROM ");
    }

    [Fact]
    public void Directory_SQL_is_local_to_the_owner_modules()
    {
        var equipment = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.MDM", "Infrastructure", "EquipmentDirectory.cs"));
        var outputScope = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.MDM", "Infrastructure", "EquipmentOutputMasterDirectory.cs"));
        var vendor = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.MDM", "Infrastructure", "VendorDirectory.cs"));
        var identity = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.SYS", "Infrastructure", "MaintenanceIdentityDirectory.cs"));

        equipment.Should().Contain("MDM_EQUIPMENT");
        outputScope.Should().Contain("MDM_EQUIPMENT");
        outputScope.Should().Contain("MDM_CARRIER");
        vendor.Should().Contain("MDM_VENDOR");
        identity.Should().Contain("SYS_USER");
        identity.Should().Contain("MDM_WORKER_USER_MAP");
    }

    [Theory]
    [InlineData("mdm.xml", "mdmModule", "equipmentDirectory", "GetEquipmentDirectory")]
    [InlineData("mdm.xml", "mdmModule", "equipmentOutputMasterDirectory", "GetEquipmentOutputMasterDirectory")]
    [InlineData("mdm.xml", "mdmModule", "vendorDirectory", "GetVendorDirectory")]
    [InlineData("sys.xml", "sysModule", "maintenanceIdentityDirectory", "GetMaintenanceIdentityDirectory")]
    public void Owner_module_xml_exports_each_directory_from_its_composition_root(
        string configFile,
        string moduleBeanId,
        string beanId,
        string factoryMethod)
    {
        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", configFile);
        var document = System.Xml.Linq.XDocument.Load(path);
        var bean = document.Descendants()
            .Single(element => element.Name.LocalName == "object"
                               && (string?)element.Attribute("id") == beanId);

        bean.Attribute("type").Should().BeNull(
            "Spring must not construct module-owned repository adapters directly");
        ((string?)bean.Attribute("factory-object")).Should().Be(moduleBeanId);
        ((string?)bean.Attribute("factory-method")).Should().Be(factoryMethod);
    }

    [Theory]
    [InlineData("equipmentDirectory", "NexaOne.Server.Gateway.EquipmentDirectoryProxy, NexaOne.Server")]
    [InlineData("equipmentOutputMasterDirectory", "NexaOne.Server.Gateway.EquipmentOutputMasterDirectoryProxy, NexaOne.Server")]
    [InlineData("vendorDirectory", "NexaOne.Server.Gateway.VendorDirectoryProxy, NexaOne.Server")]
    [InlineData("maintenanceIdentityDirectory", "NexaOne.Server.Gateway.MaintenanceIdentityDirectoryProxy, NexaOne.Server")]
    public void Parent_context_registers_only_directory_proxies(string beanId, string expectedType)
    {
        foreach (var configFile in new[] { "server.xml", "server.sqlite.xml" })
        {
            var path = RepositorySource.GetFile(
                "src", "00.Main", "NexaOne.Server", "config", "host", configFile);
            var document = System.Xml.Linq.XDocument.Load(path);
            var bean = document.Descendants()
                .Single(element => element.Name.LocalName == "object"
                                   && (string?)element.Attribute("id") == beanId);

            ((string?)bean.Attribute("type")).Should().Be(expectedType);
            var constructor = bean.Elements()
                .Single(element => element.Name.LocalName == "constructor-arg");
            ((string?)constructor.Attribute("ref")).Should().Be(
                "moduleBeanResolver",
                "parent proxies receive only the typed module resolver and no database dependency");
        }
    }

    [Fact]
    public void Equipment_output_master_contract_is_an_MDM_owned_typed_seam()
    {
        var path = RepositorySource.GetFile(
            "src", "02.Backend", "NexaOne.Common", "ServiceContracts", "Mdm",
            "IEquipmentOutputMasterDirectory.cs");
        var source = File.ReadAllText(path);

        source.Should().Contain("namespace NexaOne.ServiceContracts.Mdm;");
        source.Should().Contain("interface IEquipmentOutputMasterDirectory : INexaModuleBridge");
        source.Should().NotContain("NexaModuleBridge(",
            "shared contracts must not embed Spring module or bean metadata");
    }
}
