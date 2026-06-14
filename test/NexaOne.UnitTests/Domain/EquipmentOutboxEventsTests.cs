using System.Text.Json;
using NexaOne.MDM.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>Equipment 애그리거트 — 비활성화/활성화/상위변경 시 도메인 이벤트 발행(ADR-002).
/// UpdateInfo는 비이벤트 편집이라 발행하지 않으며, 읽기경로 재구성(Restore)도 발행 대상이 아니다.</summary>
public sealed class EquipmentOutboxEventsTests
{
    private static Equipment NewEquipment(string id) =>
        Equipment.Create(id, "Furnace #1", "P01", "A01", "Furnace").Value;

    [Fact]
    public void Deactivate_raises_EquipmentDeactivated_event_with_outbox_envelope()
    {
        var eq = NewEquipment("EQ-D1");

        eq.Deactivate();

        var ev = eq.DomainEvents.OfType<EquipmentDeactivatedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("EquipmentDeactivated");
        ev.Module.Should().Be("MDM");
        ev.AggregateId.Should().Be("EQ-D1", "설비별 순서 보장을 위해 AGGREGATE_ID는 EquipmentId여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewValidState").GetString().Should().Be("Invalid");
    }

    [Fact]
    public void Activate_raises_EquipmentActivated_event_with_outbox_envelope()
    {
        var eq = NewEquipment("EQ-A1");
        eq.Deactivate();
        eq.ClearDomainEvents();   // 비활성화 이벤트 제거 — 활성화 이벤트만 검증

        eq.Activate();

        var ev = eq.DomainEvents.OfType<EquipmentActivatedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("EquipmentActivated");
        ev.Module.Should().Be("MDM");
        ev.AggregateId.Should().Be("EQ-A1");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewValidState").GetString().Should().Be("Valid");
    }

    [Fact]
    public void ChangeParent_raises_EquipmentParentChanged_event_with_parent_payload()
    {
        var eq = NewEquipment("EQ-P1");

        eq.ChangeParent("EQ-PARENT");

        var ev = eq.DomainEvents.OfType<EquipmentParentChangedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("EquipmentParentChanged");
        ev.Module.Should().Be("MDM");
        ev.AggregateId.Should().Be("EQ-P1");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("ParentEquipmentId").GetString().Should().Be("EQ-PARENT");
    }

    [Fact]
    public void UpdateInfo_raises_no_domain_events()
    {
        var eq = NewEquipment("EQ-U1");

        eq.UpdateInfo("New Name", "desc", "NewType", "Vendor", "Model");

        eq.DomainEvents.Should().BeEmpty("UpdateInfo는 비이벤트 필드 편집이라 outbox 발행 대상이 아니다");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => Equipment.Restore("EQ-R1", "Furnace", "desc", "P01", "A01", "Furnace", null, "V", "M", "C", "Invalid")
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var eq = NewEquipment("EQ-C1");
        eq.Deactivate();
        eq.ClearDomainEvents();
        eq.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
