using NexaOne.EST.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>EquipmentCurrentState 애그리거트 — 상태 전이 시 도메인 이벤트 발행(ADR-002 대표 슬라이스).</summary>
public sealed class EquipmentCurrentStateTests
{
    [Fact]
    public void Create_has_no_domain_events()
        => EquipmentCurrentState.Create("EQ-1", "P1").DomainEvents.Should().BeEmpty("생성 시점엔 도메인 이벤트가 없어야 한다");

    [Fact]
    public void ApplyTransition_raises_EquipmentStateChanged_event_with_outbox_envelope()
    {
        var state = EquipmentCurrentState.Create("EQ-1", "P1", "IDLE");

        state.ApplyTransition("RUNNING");

        state.CurrentStateId.Should().Be("RUNNING");
        state.StateVersion.Should().Be(2, "전이 시 낙관적 동시성 버전이 증가해야 한다");

        var ev = state.DomainEvents.OfType<EquipmentStateChangedDomainEvent>().Should().ContainSingle().Subject;
        ev.EquipmentId.Should().Be("EQ-1");
        ev.NewState.Should().Be("RUNNING");
        // IOutboxEvent 봉투(리포가 EES_OUTBOX 매핑에 사용).
        ev.EventType.Should().Be("EquipmentStateChanged");
        ev.Module.Should().Be("EST");
        ev.AggregateId.Should().Be("EQ-1");
        ev.Payload.Should().Be("RUNNING");
    }

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var state = EquipmentCurrentState.Create("EQ-2", "P1");
        state.ApplyTransition("RUNNING");

        state.ClearDomainEvents();

        state.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
