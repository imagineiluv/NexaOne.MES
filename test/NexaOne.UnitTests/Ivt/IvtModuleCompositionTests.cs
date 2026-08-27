using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.UnitTests.TestInfrastructure;
using Spring.Core.IO;
using Spring.Objects.Factory.Support;
using Spring.Objects.Factory.Xml;
using IvtModule = NexaOne.IVT.Module;

namespace NexaOne.UnitTests.Ivt;

public sealed class IvtModuleCompositionTests
{
    [Fact]
    public void Module_builds_one_shared_instance_for_each_public_export()
    {
        var module = new IvtModule(
            new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = "Data Source=:memory:",
            },
            new SqliteEesDbCapability(),
            Mock.Of<IFdcTraceSource>(),
            new ConfigurationBuilder().Build());

        module.GetMaterialBridge().Should().BeAssignableTo<IMaterialBridge>();
        module.GetMaterialBridge().Should().BeSameAs(module.GetMaterialBridge());
        module.GetMaterialLotBridge().Should().BeAssignableTo<IMaterialLotBridge>();
        module.GetMaterialLotBridge().Should().BeSameAs(module.GetMaterialLotBridge());
        module.GetMaterialLotDirectory().Should().BeAssignableTo<IMaterialLotDirectory>();
        module.GetMaterialLotDirectory().Should().BeSameAs(module.GetMaterialLotDirectory());
        module.GetMrpInventoryDirectory().Should().BeAssignableTo<IMrpInventoryDirectory>();
        module.GetMrpInventoryDirectory().Should().BeSameAs(module.GetMrpInventoryDirectory());
        module.GetTraceMaterialConsumptionWorker().Should().BeAssignableTo<IHostedService>();
        module.GetTraceMaterialConsumptionWorker()
            .Should().BeSameAs(module.GetTraceMaterialConsumptionWorker());
    }

    [Fact]
    public void Spring_xml_boots_the_module_and_resolves_only_its_public_exports()
    {
        using var factory = new DefaultListableObjectFactory();
        factory.RegisterSingleton("eesDataSource", DataSource());
        factory.RegisterSingleton("eesDialect", new SqliteEesDbCapability());
        factory.RegisterSingleton("fdcTraceSource", Mock.Of<IFdcTraceSource>());
        factory.RegisterSingleton("appConfiguration", new ConfigurationBuilder().Build());

        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "ivt.xml");
        var loaded = new XmlObjectDefinitionReader(factory)
            .LoadObjectDefinitions(new FileSystemResource(path));
        factory.PreInstantiateSingletons();

        loaded.Should().Be(6);
        factory.GetObject<IMaterialBridge>("materialBridge").Should().NotBeNull();
        factory.GetObject<IMaterialLotBridge>("materialLotBridge").Should().NotBeNull();
        factory.GetObject<IMaterialLotDirectory>("materialLotDirectory").Should().NotBeNull();
        factory.GetObject<IMrpInventoryDirectory>("mrpInventoryDirectory").Should().NotBeNull();
        factory.GetObjectsOfType(typeof(IHostedService)).Keys
            .Should().ContainSingle().Which.Should().Be("traceMaterialConsumptionWorker");
    }

    private static EesDataSource DataSource() => new()
    {
        Provider = new SqliteTestDatabaseProvider(),
        ConnectionString = "Data Source=:memory:",
    };
}
