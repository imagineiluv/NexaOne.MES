using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.UnitTests.TestInfrastructure;
using Spring.Core.IO;
using Spring.Objects.Factory.Support;
using Spring.Objects.Factory.Xml;
using System.Text.Json;
using IvtModule = NexaOne.IVT.Module;

namespace NexaOne.UnitTests.Ivt;

public sealed class IvtModuleCompositionTests
{
    [Fact]
    public void Production_sample_has_one_fail_closed_trace_configuration_section()
    {
        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "appsettings.Production.sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var ivtSections = document.RootElement.EnumerateObject()
            .Where(property => property.NameEquals("Ivt"))
            .ToList();

        ivtSections.Should().ContainSingle();
        var traceConfiguration = ivtSections[0].Value.GetProperty("TraceConfiguration");
        traceConfiguration.GetProperty("BindingsEnabled").GetBoolean().Should().BeFalse();
        traceConfiguration.GetProperty("FeedSessionsEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Feed_session_commands_are_fail_closed_by_default_at_the_host_module_boundary()
    {
        var module = new IvtModule(
            DataSource(),
            new SqliteEesDbCapability(),
            Mock.Of<IFdcTraceSource>(),
            new ConfigurationBuilder().Build());

        var result = await module.GetTraceMaterialBridge().ExecuteFeedSessionAsync(
            new FeedSessionCommand(
                FeedSessionOperations.Mount, "FS-01", 0, "mount-01", "TEST", "source-01",
                DateTime.UtcNow, PlantId: "P1", EquipmentId: "E1", FeedPointId: "F1",
                MaterialLotId: "L1", MaterialId: "M1", ActorId: "operator"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IVT.FeedSession.FeatureDisabled");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Binding_commands_are_fail_closed_when_the_distributed_fence_gate_is_missing_or_false(
        bool? configured)
    {
        var settings = configured is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>
            {
                ["Ivt:TraceConfiguration:BindingsEnabled"] = configured.Value.ToString(),
            };
        var module = new IvtModule(
            DataSource(),
            new SqliteEesDbCapability(),
            Mock.Of<IFdcTraceSource>(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        var result = await module.GetTraceMaterialBridge().ExecuteBindingAsync(
            new TraceBindingCommand(
                TraceBindingOperations.Create, "BIND-01", 0, "binding-01", "TEST", "source-01",
                DateTime.UtcNow, DateTime.UtcNow, PlantId: "P1", EquipmentId: "E1",
                ParameterId: "FLOW", FeedPointId: "F1", CalculationMode: "Direct",
                ScaleFactor: 1m, OutputUnit: "kg", ActorId: "operator"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IVT.TraceBinding.FeatureDisabled");
    }

    [Fact]
    public void Binding_gate_cannot_be_enabled_before_the_durable_cross_process_fence_exists()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Ivt:TraceConfiguration:BindingsEnabled"] = "true",
            }).Build();

        Action create = () => _ = new IvtModule(
            DataSource(),
            new SqliteEesDbCapability(),
            Mock.Of<IFdcTraceSource>(),
            configuration);

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*durable cross-process maintenance fence*");
    }

    [Fact]
    public void Trace_material_worker_cannot_be_enabled_before_its_durable_and_hil_gates_exist()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Worker:Ivt:TraceMaterialConsumption:Enabled"] = "true",
            }).Build();

        Action create = () => _ = new IvtModule(
            DataSource(),
            new SqliteEesDbCapability(),
            Mock.Of<IFdcTraceSource>(),
            configuration);

        create.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "*Worker:Ivt:TraceMaterialConsumption:Enabled=true*"
                + "*FDC retention boundary*"
                + "*FeedSession PendingDrain Finalize*"
                + "*commissioned HIL evidence*");
    }

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
        module.GetTraceMaterialBridge().Should().BeAssignableTo<ITraceMaterialBridge>();
        module.GetTraceMaterialBridge().Should().BeSameAs(module.GetTraceMaterialBridge());
        module.GetMaterialLotDirectory().Should().BeAssignableTo<IMaterialLotDirectory>();
        module.GetMaterialLotDirectory().Should().BeSameAs(module.GetMaterialLotDirectory());
        module.GetMrpInventoryDirectory().Should().BeAssignableTo<IMrpInventoryDirectory>();
        module.GetMrpInventoryDirectory().Should().BeSameAs(module.GetMrpInventoryDirectory());
        module.GetFdcTraceRetentionGuard().Should().BeAssignableTo<IFdcTraceRetentionGuard>();
        module.GetFdcTraceRetentionGuard().Should().BeSameAs(module.GetFdcTraceRetentionGuard());
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

        loaded.Should().Be(8);
        factory.GetObject<IMaterialBridge>("materialBridge").Should().NotBeNull();
        factory.GetObject<IMaterialLotBridge>("materialLotBridge").Should().NotBeNull();
        factory.GetObject<ITraceMaterialBridge>("traceMaterialBridge").Should().NotBeNull();
        factory.GetObject<IMaterialLotDirectory>("materialLotDirectory").Should().NotBeNull();
        factory.GetObject<IMrpInventoryDirectory>("mrpInventoryDirectory").Should().NotBeNull();
        factory.GetObject<IFdcTraceRetentionGuard>("fdcTraceRetentionGuard").Should().NotBeNull();
        factory.GetObjectsOfType(typeof(IHostedService)).Keys
            .Should().ContainSingle().Which.Should().Be("traceMaterialConsumptionWorker");
    }

    private static EesDataSource DataSource() => new()
    {
        Provider = new SqliteTestDatabaseProvider(),
        ConnectionString = "Data Source=:memory:",
    };
}
