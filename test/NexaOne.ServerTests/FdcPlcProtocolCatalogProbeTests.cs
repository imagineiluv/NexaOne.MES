using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Server;
using NexaFramework;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>FDC PLC adapter가 외부 접속 없이도 구성 수명 주기를 정직하게 보고하는지 검증한다.</summary>
public sealed class FdcPlcProtocolCatalogProbeTests
{
    [Fact]
    public async Task Adapter_reports_disabled_without_resolving_a_spring_bean_when_modules_are_off()
    {
        var runtime = Runtime(modulesEnabled: false);
        var adapter = new FdcPlcProtocolCatalogProbe(runtime);

        var health = await adapter.CheckHealthAsync();

        adapter.Descriptor.Id.Should().Be("nexaone.fdc.plc");
        adapter.Descriptor.Kind.Should().Be("plc");
        adapter.Descriptor.Capabilities.Should().Contain(new[]
        {
            "multi-protocol-selection", "plc-read", "plc-write", "plc-subscription",
        });
        health.Status.Should().Be(ExternalDependencyHealthStatus.Disabled);
        health.Details.Should().ContainKey("driverCount").WhoseValue.Should().Be("0");
    }

    [Fact]
    public async Task Adapter_reports_unhealthy_when_required_module_runtime_has_not_started()
    {
        var runtime = Runtime(modulesEnabled: true);
        var adapter = new FdcPlcProtocolCatalogProbe(runtime);

        var health = await adapter.CheckHealthAsync();

        health.Status.Should().Be(ExternalDependencyHealthStatus.Unhealthy);
        health.Summary.Should().Contain("not started");
    }

    private static NexaOneMesRuntimeState Runtime(bool modulesEnabled) =>
        new(
            new ApplicationServer(),
            new ConfigurationBuilder().Build(),
            modulesEnabled);
}
