using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexusLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Fdc;

/// <summary>FDC 설비-엔드포인트 매핑을 NexusLogic PlcEndpoint/PlcSubscription으로 변환하는 로직을 검증한다.</summary>
public sealed class FdcEndpointMapperTests
{
    private static FdcEquipmentEndpoint Endpoint(string protocol = "OpcUa", int samplingMs = 500) =>
        FdcEquipmentEndpoint.Create("EP1", "EQ-001", protocol, "opc.tcp://host:4840", samplingMs).Value;

    private static FdcParameter Param(string id, bool active = true)
    {
        var p = FdcParameter.Create(id, $"name-{id}", "EQ-001", "C", 0m, 100m).Value;
        if (!active) p.Deactivate();
        return p;
    }

    [Fact]
    public void ToPlcEndpoint_maps_protocol_and_url()
    {
        var plc = FdcEndpointMapper.ToPlcEndpoint(Endpoint("OpcUa"));

        plc.EndpointId.Should().Be("EP1");
        plc.DriverKind.Should().Be(PlcDriverKind.OpcUa);
        plc.Host.Should().Be("host");
        plc.Port.Should().Be(4840);
        plc.Options["endpointUrl"].Should().Be("opc.tcp://host:4840",
            "OPC UA discovery URL은 scheme/path를 포함한 원문을 보존해야 한다");
    }

    [Fact]
    public void ToPlcEndpoint_parses_protocol_case_insensitively()
    {
        // 도메인은 "OpcUa"를 저장하지만 매퍼는 대소문자 무관하게 PlcDriverKind로 파싱한다
        var plc = FdcEndpointMapper.ToPlcEndpoint(
            FdcEquipmentEndpoint.Create("EP2", "EQ-001", "MitsubishiMc", "tcp://h:5007").Value);
        plc.DriverKind.Should().Be(PlcDriverKind.MitsubishiMc);
        plc.Host.Should().Be("h");
        plc.Port.Should().Be(5007);
    }

    [Theory]
    [InlineData("OpcUa", PlcDriverKind.OpcUa)]
    [InlineData("ModbusTcp", PlcDriverKind.ModbusTcp)]
    [InlineData("ModbusRtu", PlcDriverKind.ModbusRtu)]
    [InlineData("SiemensS7", PlcDriverKind.SiemensS7)]
    [InlineData("MitsubishiMc", PlcDriverKind.MitsubishiMc)]
    [InlineData("EtherNetIp", PlcDriverKind.EtherNetIp)]
    [InlineData("OmronFins", PlcDriverKind.OmronFins)]
    public void ToPlcEndpoint_maps_every_declared_protocol(string protocol, PlcDriverKind expected)
    {
        var endpoint = FdcEquipmentEndpoint.Restore("EP-KIND", "EQ-001", protocol, "tcp://plc:1234", 500, true);

        FdcEndpointMapper.ToPlcEndpoint(endpoint).DriverKind.Should().Be(expected);
    }

    [Fact]
    public void ToPlcEndpoint_rejects_drifted_unknown_protocol_with_endpoint_context()
    {
        var endpoint = FdcEquipmentEndpoint.Restore("EP-BAD", "EQ-001", "UnknownPlc", "tcp://plc:1234", 500, true);

        var act = () => FdcEndpointMapper.ToPlcEndpoint(endpoint);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EP-BAD*UnknownPlc*Supported protocols*");
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
