using System.Xml.Linq;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;

namespace NexaOne.UnitTests.Architecture;

/// <summary>IVT Spring interface를 단일 공개 조립 진입점과 bridge/worker로 제한합니다.</summary>
public sealed class IvtCompositionBoundaryTests
{
    [Fact]
    public void Trace_material_write_repositories_are_internal_behind_the_application_service()
    {
        typeof(TraceBindingRepository).IsNotPublic.Should().BeTrue();
        typeof(FeedSessionRepository).IsNotPublic.Should().BeTrue();
        typeof(TraceBindingService).IsNotPublic.Should().BeTrue();
        typeof(FeedSessionService).IsNotPublic.Should().BeTrue();
    }

    [Fact]
    public void Ivt_xml_does_not_expose_repository_or_service_implementation_types()
    {
        var path = RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "modules", "ivt.xml");
        var document = XDocument.Load(path);
        var objects = document.Descendants()
            .Where(element => element.Name.LocalName == "object")
            .ToArray();

        objects.Select(element => (string?)element.Attribute("type"))
            .Where(type => type?.StartsWith("NexaOne.IVT", StringComparison.Ordinal) == true)
            .Should().Equal("NexaOne.IVT.Module, NexaOne.IVT");

        objects.Single(element => (string?)element.Attribute("id") == "ivtModule")
            .Attribute("type")?.Value.Should().Be("NexaOne.IVT.Module, NexaOne.IVT");

        var exports = objects
            .Where(element => (string?)element.Attribute("factory-object") == "ivtModule")
            .ToDictionary(
                element => (string)element.Attribute("id")!,
                element => (string)element.Attribute("factory-method")!,
                StringComparer.Ordinal);
        exports.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["materialBridge"] = "GetMaterialBridge",
            ["materialLotBridge"] = "GetMaterialLotBridge",
            ["traceMaterialBridge"] = "GetTraceMaterialBridge",
            ["materialLotDirectory"] = "GetMaterialLotDirectory",
            ["mrpInventoryDirectory"] = "GetMrpInventoryDirectory",
            ["fdcTraceRetentionGuard"] = "GetFdcTraceRetentionGuard",
            ["traceMaterialConsumptionWorker"] = "GetTraceMaterialConsumptionWorker",
        });
    }
}
