using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Fdc;

/// <summary>FDC 설비-엔드포인트 매핑을 NexaLogic PlcEndpoint/PlcSubscription으로 변환하는 로직을 검증한다.</summary>
public sealed class FdcEndpointMapperTests
{
    private static string ExistingTagMapPath => typeof(FdcEndpointMapperTests).Assembly.Location;

    private static FdcEquipmentEndpoint Endpoint(string protocol = "ModbusTcp", int samplingMs = 500) =>
        FdcEquipmentEndpoint.Create(
            "EP1", "EQ-001", protocol, "tcp://host:502", samplingMs, ExistingTagMapPath).Value;

    private static FdcParameter Param(string id, bool active = true)
    {
        var p = FdcParameter.Create(id, $"name-{id}", "EQ-001", "C", 0m, 100m).Value;
        if (!active) p.Deactivate();
        return p;
    }

    [Fact]
    public void ToPlcEndpoint_maps_protocol_and_url()
    {
        var plc = FdcEndpointMapper.ToPlcEndpoint(Endpoint("ModbusTcp"));

        plc.EndpointId.Should().Be("EP1");
        plc.DriverKind.Should().Be(PlcDriverKind.ModbusTcp);
        plc.Host.Should().Be("host");
        plc.Port.Should().Be(502);
        plc.Options["tagMapPath"].Should().Be(Path.GetFullPath(ExistingTagMapPath));
    }

    [Fact]
    public void ToPlcEndpoint_parses_protocol_case_insensitively()
    {
        // 매퍼는 저장된 프로토콜을 대소문자 무관하게 PlcDriverKind로 파싱한다.
        var plc = FdcEndpointMapper.ToPlcEndpoint(
            FdcEquipmentEndpoint.Create(
                "EP2", "EQ-001", "MitsubishiMc", "tcp://h:5007", tagMapPath: ExistingTagMapPath).Value);
        plc.DriverKind.Should().Be(PlcDriverKind.MitsubishiMc);
        plc.Host.Should().Be("h");
        plc.Port.Should().Be(5007);
    }

    [Theory]
    [InlineData("ModbusTcp", PlcDriverKind.ModbusTcp)]
    [InlineData("SiemensS7", PlcDriverKind.SiemensS7)]
    [InlineData("MitsubishiMc", PlcDriverKind.MitsubishiMc)]
    [InlineData("EtherNetIp", PlcDriverKind.EtherNetIp)]
    public void ToPlcEndpoint_maps_every_declared_protocol(string protocol, PlcDriverKind expected)
    {
        var endpoint = FdcEquipmentEndpoint.Restore(
            "EP-KIND", "EQ-001", protocol, "tcp://plc:1234", 500, true,
            tagMapPath: ExistingTagMapPath);

        FdcEndpointMapper.ToPlcEndpoint(endpoint).DriverKind.Should().Be(expected);
    }

    [Fact]
    public void ToPlcEndpoint_rejects_drifted_unknown_protocol_with_endpoint_context()
    {
        var endpoint = FdcEquipmentEndpoint.Restore(
            "EP-BAD", "EQ-001", "UnknownPlc", "tcp://plc:1234", 500, true,
            tagMapPath: ExistingTagMapPath);

        var act = () => FdcEndpointMapper.ToPlcEndpoint(endpoint);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EP-BAD*UnknownPlc*Supported protocols*");
    }

    [Fact]
    public void ToPlcEndpoint_requires_an_existing_tag_map_before_connection()
    {
        var missing = FdcEquipmentEndpoint.Restore(
            "EP-MISSING", "EQ-001", "ModbusTcp", "tcp://plc:502", 500, true,
            tagMapPath: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var act = () => FdcEndpointMapper.ToPlcEndpoint(missing);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*EP-MISSING*tag map*");
    }

    [Fact]
    public void ToPlcEndpoint_resolves_relative_tag_map_from_application_base_directory()
    {
        var relative = Path.GetFileName(ExistingTagMapPath);
        var endpoint = FdcEquipmentEndpoint.Restore(
            "EP-REL", "EQ-001", "ModbusTcp", "tcp://plc:502", 500, true,
            tagMapPath: relative);

        var mapped = FdcEndpointMapper.ToPlcEndpoint(endpoint);

        mapped.Options["tagMapPath"].Should().Be(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative)));
    }

    [Fact]
    public void ToSubscription_uses_active_parameter_ids_as_tags_and_endpoint_interval()
    {
        var sub = FdcEndpointMapper.ToSubscription(
            Endpoint(samplingMs: 750),
            new[] { Param("TEMP01"), Param("PRESS01"), Param("OLD", active: false) });

        sub.EndpointId.Should().Be("EP1");
        sub.TagNames.Should().BeEquivalentTo("TEMP01", "PRESS01");
        sub.TagNames.Should().NotContain("OLD", "비활성 파라미터는 구독에서 제외한다");
        sub.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public void ToSubscription_yields_empty_tags_when_no_active_parameters()
    {
        var sub = FdcEndpointMapper.ToSubscription(Endpoint(), Array.Empty<FdcParameter>());
        sub.TagNames.Should().BeEmpty();
    }
}
