using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.UnitTests.Architecture;

/// <summary>QMS가 참조 마스터의 물리 스키마 대신 소유 모듈 directory seam을 사용하도록 고정합니다.</summary>
public sealed class QmsForeignSchemaBoundaryTests
{
    private static readonly string[] ForeignTables =
    {
        "POM_LOT",
        "IVT_MATERIAL_LOT",
        "MDM_EQUIPMENT",
        "MDM_PROCESS",
        "SYS_USER"
    };

    [Theory]
    [InlineData("QmsReferenceRepository.cs")]
    [InlineData("InspectionResultRepository.cs")]
    public void Qms_repositories_contain_no_foreign_schema_SQL(string fileName)
    {
        var path = RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.QMS", "Infrastructure", fileName);
        var source = File.ReadAllText(path);

        foreach (var table in ForeignTables)
            source.Should().NotContain(table);
    }

    [Theory]
    [InlineData("NexaOne.POM", "ProductionLotDirectory.cs", "POM_LOT")]
    [InlineData("NexaOne.IVT", "MaterialLotDirectory.cs", "IVT_MATERIAL_LOT")]
    [InlineData("NexaOne.MDM", "ProcessDirectory.cs", "MDM_PROCESS")]
    [InlineData("NexaOne.SYS", "UserDirectory.cs", "SYS_USER")]
    public void Foreign_SQL_is_local_to_the_owner_directory(
        string module,
        string fileName,
        string ownedTable)
    {
        var path = RepositorySource.GetFile(
            "src", "04.Modules", module, "Infrastructure", fileName);
        File.ReadAllText(path).Should().Contain(ownedTable);
    }

    [Theory]
    [InlineData("ProductionLotDirectoryProxy.cs", "Pom", "productionLotDirectory")]
    [InlineData("MaterialLotDirectoryProxy.cs", "Ivt", "materialLotDirectory")]
    [InlineData("ProcessDirectoryProxy.cs", "Mdm", "processDirectory")]
    [InlineData("UserDirectoryProxy.cs", "Sys", "userDirectory")]
    public void Host_adapters_are_SQL_free_sibling_context_proxies(
        string fileName,
        string module,
        string beanName)
    {
        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "Gateway", fileName);
        var source = File.ReadAllText(path);

        source.Should().Contain("_resolver.Resolve<");
        source.Should().Contain($"\"{module}\"");
        source.Should().Contain($"\"{beanName}\"");
        source.Should().NotContain("ApplicationServer.GetInstance()");
        source.Should().NotContain("EesDataSource");
        source.Should().NotContain("QueryRepository");
        source.Should().NotContain("SELECT ");
        source.Should().NotContain("FROM ");
    }

    [Theory]
    [InlineData("pom.xml", "productionLotDirectory", "pomModule", "GetProductionLotDirectory")]
    [InlineData("mdm.xml", "processDirectory", "mdmModule", "GetProcessDirectory")]
    [InlineData("sys.xml", "userDirectory", "sysModule", "GetUserDirectory")]
    public void Owner_module_xml_exports_directory_from_composition_root(
        string configFile,
        string beanId,
        string moduleBean,
        string factoryMethod)
    {
        var bean = ModuleBean(configFile, beanId);
        ((string?)bean.Attribute("factory-object")).Should().Be(moduleBean);
        ((string?)bean.Attribute("factory-method")).Should().Be(factoryMethod);
    }

    [Fact]
    public void Ivt_xml_exports_material_directory_from_its_composition_module()
    {
        var bean = ModuleBean("ivt.xml", "materialLotDirectory");
        ((string?)bean.Attribute("factory-object")).Should().Be("ivtModule");
        ((string?)bean.Attribute("factory-method")).Should().Be("GetMaterialLotDirectory");
    }

    [Theory]
    [InlineData("productionLotDirectory", "NexaOne.Server.Gateway.ProductionLotDirectoryProxy, NexaOne.Server")]
    [InlineData("materialLotDirectory", "NexaOne.Server.Gateway.MaterialLotDirectoryProxy, NexaOne.Server")]
    [InlineData("processDirectory", "NexaOne.Server.Gateway.ProcessDirectoryProxy, NexaOne.Server")]
    [InlineData("userDirectory", "NexaOne.Server.Gateway.UserDirectoryProxy, NexaOne.Server")]
    public void Parent_context_registers_only_directory_proxy(string beanId, string expectedType)
    {
        foreach (var configFile in new[] { "server.xml", "server.sqlite.xml" })
        {
            var path = RepositorySource.GetFile(
                "src", "00.Main", "NexaOne.Server", "config", "host", configFile);
            var document = System.Xml.Linq.XDocument.Load(path);
            var bean = document.Descendants().Single(element =>
                element.Name.LocalName == "object"
                && (string?)element.Attribute("id") == beanId);

            ((string?)bean.Attribute("type")).Should().Be(expectedType);
            bean.Elements().Should().ContainSingle(
                "parent proxies must receive only the shared module resolver");
            ((string?)bean.Elements().Single().Attribute("ref"))
                .Should().Be("moduleBeanResolver");
        }
    }

    [Fact]
    public void Qms_xml_injects_owner_directories_in_constructor_order()
    {
        var module = ModuleBean("qms.xml", "qmsModule");
        module.Elements().Select(element => (string?)element.Attribute("ref"))
            .Should().Equal(
                "eesDataSource",
                "appConfiguration",
                "productionLotDirectory",
                "materialLotDirectory",
                "equipmentDirectory",
                "processDirectory",
                "userDirectory");
    }

    [Fact]
    public void Owner_directory_contracts_are_container_neutral_and_declared_by_the_host_catalog()
    {
        var expected = new Type[]
        {
            typeof(IProductionLotDirectory),
            typeof(IMaterialLotDirectory),
            typeof(IProcessDirectory),
            typeof(IUserDirectory),
        };
        expected.Should().OnlyContain(type => typeof(INexaModuleBridge).IsAssignableFrom(type));
        foreach (var type in expected)
        {
            var source = File.ReadAllText(RepositorySource.GetFile(
                "src", "02.Backend", "NexaOne.Common", "ServiceContracts",
                type.Namespace!["NexaOne.ServiceContracts.".Length..], type.Name + ".cs"));
            source.Should().NotContain("NexaModuleBridge(");
        }

        var catalogSource = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "Hosting", "NexaOneMesBridgeCatalog.cs"));
        foreach (var type in expected)
            catalogSource.Should().Contain($"Bind<{type.Name}>");
    }

    private static System.Xml.Linq.XElement ModuleBean(string configFile, string beanId)
    {
        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", configFile);
        var document = System.Xml.Linq.XDocument.Load(path);
        return document.Descendants().Single(element =>
            element.Name.LocalName == "object"
            && (string?)element.Attribute("id") == beanId);
    }
}
