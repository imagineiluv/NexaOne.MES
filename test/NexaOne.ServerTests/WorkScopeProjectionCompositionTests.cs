using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM;
using NexaOne.Server;
using NexaOne.ServiceContracts.Pom;
using NexaDB.Data.Sqlite;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class WorkScopeProjectionCompositionTests
{
    private static readonly XNamespace Spring = "http://www.springframework.net";

    [Fact]
    public void Pom_only_off_keeps_acceptance_and_schema_without_application_policy_or_worker()
    {
        var pom = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "pom.xml"));
        var objects = pom.Root!.Elements(Spring + "object").ToList();

        objects.Select(ObjectId).Should().Contain(
            ["workScopeProjectionBridge", "pomWorkScopeProjectionSqliteSchemaContribution"]);
        objects.Select(ObjectId).Should().NotContain(
            ["workScopeProjectionApplicationModule", "workScopeProjectionRuntime", "workScopeProjectionWorker"]);
        objects.Single(item => ObjectId(item) == "pomModule")
            .Elements(Spring + "constructor-arg")
            .Select(item => (string?)item.Attribute("ref"))
            .Should().NotContain("workScopeProjectionPolicy");
        typeof(NexaOne.POM.Module).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should().NotContain(typeof(IWorkScopeProjectionPolicy));

        var validate = () => NexaOneMesRuntimeState.ValidateWorkScopeProjectionRuntime(
            enabled: false,
            Array.Empty<IWorkScopeProjectionRuntime>(),
            Array.Empty<IHostedService>());

        validate.Should().NotThrow(
            "a POM-only deployment with the optional application feature disabled is supported");
    }

    [Fact]
    public void Pom_only_on_fails_fast_when_the_optional_application_marker_is_absent()
    {
        var validate = () => NexaOneMesRuntimeState.ValidateWorkScopeProjectionRuntime(
            enabled: true,
            Array.Empty<IWorkScopeProjectionRuntime>(),
            Array.Empty<IHostedService>());

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*Enabled=true*IWorkScopeProjectionRuntime*pom-projection.xml*");
    }

    [Fact]
    public void Cleaner_on_composes_one_default_alc_marker_as_the_same_hosted_worker()
    {
        var app = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "app.xml"));
        var pomService = app.Root!
            .Element("Services")!
            .Elements("Service")
            .Single(service => (string?)service.Attribute("name") == "Pom");
        ((string)pomService.Attribute("configFiles")!).Split(';')
            .Should().Equal(
                "config/modules/pom.xml",
                "config/projects/cleaner.xml",
                "config/modules/pom-projection.xml");
        ((string)pomService.Attribute("classPaths")!).Split(';')
            .Should().Equal(
                "./Modules/NexaOne.POM.dll",
                "./Modules/NexaOne.Project.Cleaner.dll");

        var projection = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "pom-projection.xml"));
        var projectionObjects = projection.Root!.Elements(Spring + "object").ToList();
        var applicationModule = projectionObjects.Single(
            item => ObjectId(item) == "workScopeProjectionApplicationModule");
        applicationModule.Elements(Spring + "constructor-arg")
            .Select(item => (string?)item.Attribute("ref"))
            .Should().Equal("eesDataSource", "appConfiguration", "workScopeProjectionPolicy");
        projectionObjects.Single(item => ObjectId(item) == "workScopeProjectionRuntime")
            .Attribute("factory-method")!.Value.Should().Be("GetWorkScopeProjectionRuntime");
        projectionObjects.Single(item => ObjectId(item) == "workScopeProjectionWorker")
            .Attribute("factory-method")!.Value.Should().Be("GetWorkScopeProjectionWorker");

        var module = new WorkScopeProjectionApplicationModule(
            new EesDataSource
            {
                Provider = new SqliteProvider(),
                ConnectionString = "Data Source=:memory:",
            },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worker:Pom:WorkScopeProjection:Enabled"] = "true",
                })
                .Build(),
            new ObservePolicy());
        var marker = module.GetWorkScopeProjectionRuntime();
        var worker = module.GetWorkScopeProjectionWorker();

        ReferenceEquals(marker, worker).Should().BeTrue(
            "Spring marker and hosted-service exports must share one lifecycle object");
        var validate = () => NexaOneMesRuntimeState.ValidateWorkScopeProjectionRuntime(
            enabled: true,
            [marker],
            [worker]);
        validate.Should().NotThrow();
    }

    [Fact]
    public void Runtime_guard_rejects_duplicate_markers_and_split_lifecycle_objects()
    {
        var first = new RuntimeStub();
        var second = new RuntimeStub();

        var duplicate = () => NexaOneMesRuntimeState.ValidateWorkScopeProjectionRuntime(
            enabled: true,
            [first, second],
            [first, second]);
        duplicate.Should().Throw<InvalidOperationException>()
            .WithMessage("*more than one*IWorkScopeProjectionRuntime*");

        var split = () => NexaOneMesRuntimeState.ValidateWorkScopeProjectionRuntime(
            enabled: true,
            [first],
            [second]);
        split.Should().Throw<InvalidOperationException>()
            .WithMessage("*same object*IHostedService*");
    }

    private static string? ObjectId(XElement element) => (string?)element.Attribute("id");

    private sealed class ObservePolicy : IWorkScopeProjectionPolicy
    {
        public WorkScopeProjectionPolicyIdentity Identity { get; } = new("composition-test", "1");

        public WorkScopeProjectionDecision Decide(WorkScopeProjectionContext context) =>
            WorkScopeProjectionDecision.Observe("CompositionTest");
    }

    private sealed class RuntimeStub : IWorkScopeProjectionRuntime, IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
