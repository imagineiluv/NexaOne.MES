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
            ["workScopeProjectionApplicationModule", "workScopeProjectionRuntime", "workScopeProjectionWorker",
             "workScopeProjectionAuthorityValidator"]);
        var pomModule = objects.Single(item => ObjectId(item) == "pomModule");
        var pomModuleReferences = pomModule
            .Elements(Spring + "constructor-arg")
            .Select(item => (string?)item.Attribute("ref"))
            .ToList();
        pomModuleReferences.Should().NotContain(
            ["workScopeProjectionPolicy", "workScopeProjectionAuthorityValidator"],
            "the core manifest must not bind a product policy or child validator target directly");
        pomModuleReferences.Should().HaveCount(11);
        pomModuleReferences[^1].Should().Be("workScopeProjectionAuthorityValidatorProxy",
            "Spring uses the lazy fail-closed parent seam while direct construction keeps its default");
        pomModule.Attribute("factory-method")!.Value.Should().Be(
            "CreateWithProjectionAuthorityValidatorV2",
            "Spring must select V2 without adding an ambiguous same-arity constructor");
        var constructors = typeof(NexaOne.POM.Module).GetConstructors();
        constructors.SelectMany(static constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Should().NotContain(typeof(IWorkScopeProjectionPolicy));
        var compatibilityConstructor = constructors.Single(
            static constructor => constructor.GetParameters().Length == 10);
        compatibilityConstructor.GetParameters()[^1].HasDefaultValue.Should().BeTrue(
            "existing direct consumers may omit IEquipmentOutputMasterDirectory");
        constructors.Should().ContainSingle(
            static constructor => constructor.GetParameters().Length == 11
                && constructor.GetParameters().Last().ParameterType
                    == typeof(IWorkScopeProjectionAuthorityValidator),
            "the committed legacy 11-argument constructor ABI must remain exact");
        constructors.Should().NotContain(
            static constructor => constructor.GetParameters().Length == 11
                && constructor.GetParameters().Last().ParameterType
                    == typeof(IWorkScopeProjectionAuthorityValidatorV2),
            "same-arity legacy and V2 constructors make null, dual-interface, and Spring selection ambiguous");
        typeof(NexaOne.POM.Module).GetMethod("CreateWithProjectionAuthorityValidatorV2")!
            .GetParameters()[^1].ParameterType.Should().Be(
                typeof(IWorkScopeProjectionAuthorityValidatorV2));

        var legacyValidate = typeof(IWorkScopeProjectionAuthorityValidator)
            .GetMethod(nameof(IWorkScopeProjectionAuthorityValidator.ValidateAsync))!;
        legacyValidate.ReturnType.Should().Be(
            typeof(Task<NexaOne.Common.Result<WorkScopeProjectionAuthorityEvidence>>),
            "validators compiled against the committed interface must retain their method slot");
        typeof(IWorkScopeProjectionAuthorityValidatorV2)
            .GetMethod(nameof(IWorkScopeProjectionAuthorityValidatorV2.ValidateAsync))!
            .ReturnType.Should().Be(typeof(Task<WorkScopeProjectionAuthorityValidationDecision>));

        var validate = () => NexaOneMesRuntimeState.ValidateWorkScopeProjectionRuntime(
            enabled: false,
            Array.Empty<IWorkScopeProjectionRuntime>(),
            Array.Empty<IHostedService>());

        validate.Should().NotThrow(
            "a POM-only deployment with the optional application feature disabled is supported");
    }

    [Fact]
    public void Authority_composition_uses_distinct_parent_proxies_and_exact_child_targets()
    {
        foreach (var hostName in new[] { "server.xml", "server.sqlite.xml" })
        {
            var host = XDocument.Load(RepositorySource.GetFile(
                "src", "00.Main", "NexaOne.Server", "config", "host", hostName));
            var hostObjects = host.Root!.Elements(Spring + "object").ToList();
            hostObjects.Select(ObjectId).Should().Contain(
            [
                "workScopeProjectionAuthorityValidatorProxy",
                "canonicalRecipeExecutionEvidenceDirectoryProxy",
                "releasedProgramArtifactDirectoryProxy",
            ]);
            hostObjects.Select(ObjectId).Should().NotContain("workScopeProjectionAuthorityValidator",
                "a PomOnly child must not fall back to a same-named parent proxy and recurse");

            hostObjects.Single(item => ObjectId(item) == "workScopeProjectionAuthorityValidatorProxy")
                .Attribute("type")!.Value.Should().Be(
                    "NexaOne.Server.Gateway.WorkScopeProjectionAuthorityValidatorProxy, NexaOne.Server");
        }

        var cleaner = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "projects", "cleaner.xml"));
        var cleanerObjects = cleaner.Root!.Elements(Spring + "object").ToList();
        cleanerObjects.Select(ObjectId).Should().Contain(
            ["cleanerProjectionAuthorityProfile", "workScopeProjectionAuthorityValidator"]);
        cleanerObjects.Single(item => ObjectId(item) == "cleanerProjectionAuthorityProfile")
            .Elements(Spring + "constructor-arg")
            .Select(item => (string?)item.Attribute("ref"))
            .Should().Equal("appConfiguration");
        cleanerObjects.Single(item => ObjectId(item) == "workScopeProjectionAuthorityValidator")
            .Elements(Spring + "constructor-arg")
            .Select(item => (string?)item.Attribute("ref"))
            .Should().Equal(
                "workScopeAuthorityEvidenceDirectory",
                "canonicalRecipeExecutionEvidenceDirectoryProxy",
                "releasedProgramArtifactDirectoryProxy",
                "cleanerProjectionAuthorityProfile");

        var rms = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "rms.xml"));
        rms.Root!.Elements(Spring + "object").Select(ObjectId)
            .Should().Contain("canonicalRecipeExecutionEvidenceDirectory");
        var sys = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "sys.xml"));
        sys.Root!.Elements(Spring + "object").Select(ObjectId)
            .Should().Contain("releasedProgramArtifactDirectory");
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
