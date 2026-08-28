using System.Xml.Linq;

namespace NexaOne.UnitTests.Architecture;

/// <summary>
/// 호스트 orchestration adapter가 업무 물리 스키마를 다시 소유하지 않고, 조회 SQL이 MDM/RMS/QMS/POM
/// owner adapter에 머무는지 검증합니다.
/// </summary>
public sealed class HostOwnerQueryBoundaryTests
{
    private static readonly string RepoRoot = RepositorySource.Root;
    private static readonly string ServerRoot = Path.Combine(
        RepoRoot, "src", "00.Main", "NexaOne.Server");

    [Theory]
    [InlineData("TrackingMasterGateway.cs")]
    [InlineData("OeeEvidenceSource.cs")]
    public void Host_orchestration_adapters_contain_no_physical_sql(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(ServerRoot, "Gateway", fileName));

        source.Should().NotContain("QueryRepository");
        source.Should().NotContain("EesDataSource");
        source.Should().NotContain("SELECT ");
        source.Should().NotContain(" FROM ");
        source.Should().NotContain("MDM_");
        source.Should().NotContain("RMS_");
        source.Should().NotContain("QMS_");
        source.Should().NotContain("POM_");
    }

    [Theory]
    [InlineData("NexaOne.MDM", "Infrastructure/TrackingRoutingDirectory.cs", "MDM_", "RMS_", "QMS_", "POM_")]
    [InlineData("NexaOne.MDM", "Infrastructure/OeePlanDirectory.cs", "MDM_", "RMS_", "QMS_", "POM_")]
    [InlineData("NexaOne.RMS", "Infrastructure/TrackingRecipeDirectory.cs", "RMS_", "MDM_", "QMS_", "POM_")]
    [InlineData("NexaOne.QMS", "Infrastructure/TrackingDefectDirectory.cs", "QMS_", "MDM_", "RMS_", "POM_")]
    [InlineData("NexaOne.POM", "Infrastructure/OeeProductionDirectory.cs", "POM_", "MDM_", "RMS_", "QMS_")]
    public void Owner_query_adapter_reads_only_its_physical_schema(
        string module,
        string relativePath,
        string ownedPrefix,
        string forbiddenOne,
        string forbiddenTwo,
        string forbiddenThree)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "04.Modules",
            module,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        source.Should().Contain(ownedPrefix, "owner adapter must prove a non-empty physical query path");
        source.Should().NotContain(forbiddenOne);
        source.Should().NotContain(forbiddenTwo);
        source.Should().NotContain(forbiddenThree);
    }

    [Fact]
    public void Spring_composition_exposes_owner_directories_and_injects_only_their_proxies()
    {
        var moduleBindings = new[]
        {
            (File: "mdm.xml", Bean: "trackingRoutingDirectory", Module: "mdmModule", Method: "GetTrackingRoutingDirectory"),
            (File: "mdm.xml", Bean: "oeePlanDirectory", Module: "mdmModule", Method: "GetOeePlanDirectory"),
            (File: "rms.xml", Bean: "trackingRecipeDirectory", Module: "rmsModule", Method: "GetTrackingRecipeDirectory"),
            (File: "qms.xml", Bean: "trackingDefectDirectory", Module: "qmsModule", Method: "GetTrackingDefectDirectory"),
            (File: "pom.xml", Bean: "oeeProductionDirectory", Module: "pomModule", Method: "GetOeeProductionDirectory"),
        };
        foreach (var binding in moduleBindings)
        {
            var document = XDocument.Load(Path.Combine(
                ServerRoot, "config", "modules", binding.File));
            var bean = document.Descendants()
                .Single(element => element.Name.LocalName == "object"
                                   && (string?)element.Attribute("id") == binding.Bean);
            ((string?)bean.Attribute("factory-object")).Should().Be(binding.Module);
            ((string?)bean.Attribute("factory-method")).Should().Be(binding.Method);
        }

        foreach (var configFile in new[] { "server.xml", "server.sqlite.xml" })
        {
            var document = XDocument.Load(Path.Combine(
                ServerRoot, "config", "host", configFile));
            var objects = document.Descendants()
                .Where(element => element.Name.LocalName == "object")
                .ToDictionary(element => (string)element.Attribute("id")!, StringComparer.Ordinal);

            ConstructorRefs(objects["trackingMasterGateway"]).Should().Equal(
                "equipmentDirectory",
                "trackingRoutingDirectory",
                "trackingRecipeDirectory",
                "trackingDefectDirectory");
            ConstructorRefs(objects["oeeEvidenceSource"]).Should().Equal(
                "oeePlanDirectory",
                "oeeProductionDirectory");
        }
    }

    [Fact]
    public void Production_runtime_uses_explicit_host_catalog_not_common_reflection_discovery()
    {
        var registration = File.ReadAllText(Path.Combine(
            ServerRoot, "Hosting", "NexaOneMesServiceCollectionExtensions.cs"));
        var catalog = File.ReadAllText(Path.Combine(
            ServerRoot, "Hosting", "NexaOneMesBridgeCatalog.cs"));

        registration.Should().Contain("NexaOneMesBridgeCatalog.Create()");
        registration.Should().NotContain("NexaModuleBridgeCatalog.Discover");
        catalog.Should().NotContain("System.Reflection");
        catalog.Should().NotContain("GetTypes()");
        catalog.Should().Contain("Bind<ITrackingRoutingDirectory>");
        catalog.Should().Contain("Bind<IOeeProductionDirectory>");
    }

    private static IReadOnlyList<string> ConstructorRefs(XElement element)
        => element.Elements()
            .Where(child => child.Name.LocalName == "constructor-arg")
            .Select(child => (string?)child.Attribute("ref"))
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();
}
