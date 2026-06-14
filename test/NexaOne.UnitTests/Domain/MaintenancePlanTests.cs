using System.Text.Json;
using NexaOne.CMMS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>MaintenancePlan 애그리거트 — 착수/완료/취소 시 도메인 이벤트 발행(ADR-002). Restore는 무발행(읽기경로).</summary>
public sealed class MaintenancePlanTests
{
    private static readonly DateTime Scheduled = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MaintenancePlan NewPlanned() =>
        MaintenancePlan.Create("PL-1", "월간 점검", "EQ-1", "PM", "Monthly", Scheduled, 2.5m, "USR-1").Value;

    [Fact]
    public void Start_raises_MaintenancePlanStarted_event_with_outbox_envelope()
    {
        var plan = NewPlanned();

        plan.Start().IsSuccess.Should().BeTrue();

        var ev = plan.DomainEvents.OfType<MaintenancePlanStartedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("MaintenancePlanStarted");
        ev.Module.Should().Be("CMMS");
        ev.AggregateId.Should().Be("PL-1", "AGGREGATE_ID는 PLAN_ID여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-1");
        doc.RootElement.GetProperty("AssigneeId").GetString().Should().Be("USR-1");
        doc.RootElement.GetProperty("ScheduledDate").GetDateTime().Should().Be(Scheduled);
    }

    [Fact]
    public void Complete_raises_MaintenancePlanCompleted_event_with_outbox_envelope()
    {
        var plan = NewPlanned();
        plan.Start();
        plan.ClearDomainEvents();   // 착수 이벤트 제거 — 완료 이벤트만 검증

        plan.Complete().IsSuccess.Should().BeTrue();

        var ev = plan.DomainEvents.OfType<MaintenancePlanCompletedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("MaintenancePlanCompleted");
        ev.Module.Should().Be("CMMS");
        ev.AggregateId.Should().Be("PL-1");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-1");
    }

    [Fact]
    public void Cancel_raises_MaintenancePlanCancelled_event_with_outbox_envelope()
    {
        var plan = NewPlanned();

        plan.Cancel().IsSuccess.Should().BeTrue();

        var ev = plan.DomainEvents.OfType<MaintenancePlanCancelledDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("MaintenancePlanCancelled");
        ev.Module.Should().Be("CMMS");
        ev.AggregateId.Should().Be("PL-1");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-1");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => MaintenancePlan.Restore("PL-2", "월간 점검", "EQ-1", "PM", "Monthly", Scheduled, 2.5m, "USR-1",
                MaintenancePlanStatus.Completed)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var plan = NewPlanned();
        plan.Start();
        plan.ClearDomainEvents();
        plan.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
