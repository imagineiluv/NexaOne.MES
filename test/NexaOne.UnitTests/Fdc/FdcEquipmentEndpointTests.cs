using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>설비↔엔드포인트 매핑 도메인(FDC_EQUIPMENT_ENDPOINT)의 생성 검증·상태 전이를 확인한다.</summary>
public sealed class FdcEquipmentEndpointTests
{
    [Fact]
    public void Create_succeeds_for_opcua_endpoint()
    {
        var result = FdcEquipmentEndpoint.Create("EP1", "EQ-001", "OpcUa", "opc.tcp://host:4840", 500);

        result.IsFailure.Should().BeFalse();
        var e = result.Value;
        e.EquipmentId.Should().Be("EQ-001");
        e.Protocol.Should().Be("OpcUa");
        e.EndpointUrl.Should().Be("opc.tcp://host:4840");
        e.SamplingIntervalMs.Should().Be(500);
        e.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("ModbusTcp")]
    [InlineData("SiemensS7")]
    [InlineData("MitsubishiMc")]
    [InlineData("EtherNetIp")]
    [InlineData("OmronFins")]
    public void Create_accepts_all_nexuslogic_protocols(string protocol)
        => FdcEquipmentEndpoint.Create("EP1", "EQ-001", protocol, "tcp://host:502").IsFailure.Should().BeFalse();

    [Theory]
    [InlineData("", "EQ-001", "OpcUa", "opc.tcp://h:1")]   // endpointId 누락
    [InlineData("EP1", "", "OpcUa", "opc.tcp://h:1")]      // equipmentId 누락
    [InlineData("EP1", "EQ-001", "Carrier", "x")]          // 미지원 프로토콜
    [InlineData("EP1", "EQ-001", "OpcUa", "")]             // URL 누락
    public void Create_fails_on_invalid_input(string id, string eq, string protocol, string url)
        => FdcEquipmentEndpoint.Create(id, eq, protocol, url).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_fails_on_non_positive_sampling_interval()
        => FdcEquipmentEndpoint.Create("EP1", "EQ-001", "OpcUa", "opc.tcp://h:1", 0).IsFailure.Should().BeTrue();

    [Fact]
    public void Deactivate_sets_inactive()
    {
        var e = FdcEquipmentEndpoint.Create("EP1", "EQ-001", "OpcUa", "opc.tcp://h:1").Value;
        e.Deactivate();
        e.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Update_helpers_apply_only_valid_values()
    {
        var e = FdcEquipmentEndpoint.Create("EP1", "EQ-001", "OpcUa", "opc.tcp://h:1", 1000).Value;

        e.UpdateUrl("opc.tcp://new:4840");
        e.SetSamplingInterval(250);
        e.EndpointUrl.Should().Be("opc.tcp://new:4840");
        e.SamplingIntervalMs.Should().Be(250);

        e.UpdateUrl("   ");          // 무효값 무시
        e.SetSamplingInterval(0);    // 무효값 무시
        e.EndpointUrl.Should().Be("opc.tcp://new:4840");
        e.SamplingIntervalMs.Should().Be(250);
    }
}
