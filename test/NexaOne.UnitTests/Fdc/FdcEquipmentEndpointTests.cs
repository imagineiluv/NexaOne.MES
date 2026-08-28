using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>설비↔엔드포인트 매핑 도메인(FDC_EQUIPMENT_ENDPOINT)의 생성 검증·상태 전이를 확인한다.</summary>
public sealed class FdcEquipmentEndpointTests
{
    [Fact]
    public void Create_succeeds_for_modbus_tcp_endpoint()
    {
        var result = FdcEquipmentEndpoint.Create("EP1", "EQ-001", "ModbusTcp", "tcp://host:502", 500);

        result.IsFailure.Should().BeFalse();
        var e = result.Value;
        e.EquipmentId.Should().Be("EQ-001");
        e.Protocol.Should().Be("ModbusTcp");
        e.EndpointUrl.Should().Be("tcp://host:502");
        e.TagMapPath.Should().BeNull("the nullable migration preserves existing endpoint rows");
        e.SamplingIntervalMs.Should().Be(500);
        e.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("SiemensS7")]
    [InlineData("MitsubishiMc")]
    [InlineData("EtherNetIp")]
    public void Create_accepts_all_currently_installable_nexalogic_protocols(string protocol)
        => FdcEquipmentEndpoint.Create("EP1", "EQ-001", protocol, "tcp://host:502").IsFailure.Should().BeFalse();

    [Theory]
    [InlineData("OpcUa")]
    [InlineData("ModbusRtu")]
    [InlineData("OmronFins")]
    public void Create_rejects_protocols_without_an_atomic_subscription_snapshot_implementation(string protocol)
        => FdcEquipmentEndpoint.Create("EP1", "EQ-001", protocol, "tcp://host:502").IsFailure.Should().BeTrue();

    [Theory]
    [InlineData("", "EQ-001", "ModbusTcp", "tcp://h:502")] // endpointId 누락
    [InlineData("EP1", "", "ModbusTcp", "tcp://h:502")]    // equipmentId 누락
    [InlineData("EP1", "EQ-001", "Carrier", "x")]          // 미지원 프로토콜
    [InlineData("EP1", "EQ-001", "ModbusTcp", "")]         // URL 누락
    public void Create_fails_on_invalid_input(string id, string eq, string protocol, string url)
        => FdcEquipmentEndpoint.Create(id, eq, protocol, url).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_fails_on_non_positive_sampling_interval()
        => FdcEquipmentEndpoint.Create("EP1", "EQ-001", "ModbusTcp", "tcp://h:502", 0).IsFailure.Should().BeTrue();

    [Fact]
    public void Deactivate_sets_inactive()
    {
        var e = FdcEquipmentEndpoint.Create("EP1", "EQ-001", "ModbusTcp", "tcp://h:502").Value;
        e.Deactivate();
        e.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Update_helpers_apply_only_valid_values()
    {
        var e = FdcEquipmentEndpoint.Create("EP1", "EQ-001", "ModbusTcp", "tcp://h:502", 1000).Value;

        e.UpdateUrl("tcp://new:502");
        e.SetSamplingInterval(250);
        e.EndpointUrl.Should().Be("tcp://new:502");
        e.SamplingIntervalMs.Should().Be(250);

        e.UpdateUrl("   ");          // 무효값 무시
        e.SetSamplingInterval(0);    // 무효값 무시
        e.EndpointUrl.Should().Be("tcp://new:502");
        e.SamplingIntervalMs.Should().Be(250);
    }

    [Fact]
    public void Restore_preserves_audit_and_state_without_revalidation()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updated = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        // 영속 후 프로토콜 표기가 AllowedProtocols 밖으로 드리프트해도 Restore는 검증하지 않아 행을 드롭하지 않고 값을 보존한다.
        var e = FdcEquipmentEndpoint.Restore(
            "EP1", "EQ-001", "LegacyProto", "tcp://h:1", 750, isActive: false,
            createdBy: "seeder", createdAt: created, updatedBy: "editor", updatedAt: updated);

        e.Should().NotBeNull();
        e.Protocol.Should().Be("LegacyProto");   // 검증 없이 보존(읽기 시 행 드롭 없음)
        e.SamplingIntervalMs.Should().Be(750);
        e.IsActive.Should().BeFalse();            // 영속 비활성 상태 보존
        e.CreatedBy.Should().Be("seeder");        // 감사 메타데이터 보존(매 읽기 UtcNow/"" 리셋 없음)
        e.CreatedAt.Should().Be(created);
        e.UpdatedBy.Should().Be("editor");
        e.UpdatedAt.Should().Be(updated);
    }
}
