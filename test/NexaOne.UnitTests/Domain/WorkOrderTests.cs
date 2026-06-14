using System.Text.Json;
using NexaOne.CMMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class WorkOrderTests
{
    private static readonly DateTime Issued = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static WorkOrder IssuedWo() =>
        WorkOrder.Create("WO001", "EQ001", "PM", "Scheduled maintenance", "tech01", Issued).Value;

    [Fact]
    public void Create_work_order_starts_in_Issued()
    {
        var wo = IssuedWo();
        wo.Status.Should().Be(WorkOrderStatus.Issued);
        wo.StartedAt.Should().BeNull();
    }

    [Fact]
    public void Create_with_invalid_type_fails()
    {
        var result = WorkOrder.Create("WO002", "EQ001", "INVALID", "desc", "tech01", Issued);
        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("PM");
    }

    [Fact]
    public void Create_with_empty_equipment_fails()
    {
        var result = WorkOrder.Create("WO003", "", "PM", "desc", "tech01", Issued);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_moves_to_InProgress()
    {
        var wo = IssuedWo();
        wo.Start().IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.InProgress);
        wo.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public void Start_from_non_Issued_fails()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Start().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Complete_moves_to_Completed()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Complete("done").IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Completed);
        wo.CompletedAt.Should().NotBeNull();
        wo.Remark.Should().Be("done");
    }

    [Fact]
    public void Complete_from_non_InProgress_fails()
    {
        var wo = IssuedWo();
        wo.Complete().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Issued_succeeds()
    {
        var wo = IssuedWo();
        wo.Cancel().IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_from_Completed_fails()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Complete();
        wo.Cancel().IsFailure.Should().BeTrue();
    }

    // ── ADR-002: 도메인 이벤트 발행(착수/완료/취소) — 리포가 같은 트랜잭션에 EES_OUTBOX로 기록한다(opt-in) ──

    [Fact]
    public void Start_raises_WorkOrderStarted_event_with_outbox_envelope()
    {
        var wo = WorkOrder.Create("WO-S", "EQ-S", "PM", "점검", "tech01", Issued).Value;

        wo.Start().IsSuccess.Should().BeTrue();

        var ev = wo.DomainEvents.OfType<WorkOrderStartedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("WorkOrderStarted");
        ev.Module.Should().Be("CMMS");
        ev.AggregateId.Should().Be("WO-S", "작업지시별 순서 보장을 위해 AGGREGATE_ID는 WO_ID여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-S");
        doc.RootElement.GetProperty("AssigneeId").GetString().Should().Be("tech01");
        doc.RootElement.GetProperty("StartedAt").GetDateTime().Should().Be(wo.StartedAt!.Value);
    }

    [Fact]
    public void Complete_raises_WorkOrderCompleted_event_with_outbox_envelope()
    {
        var wo = WorkOrder.Create("WO-C", "EQ-C", "CM", "수리", "tech01", Issued).Value;
        wo.Start();
        wo.ClearDomainEvents();   // 착수 이벤트 제거 — 완료 이벤트만 검증

        wo.Complete("정상 완료").IsSuccess.Should().BeTrue();

        var ev = wo.DomainEvents.OfType<WorkOrderCompletedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("WorkOrderCompleted");
        ev.Module.Should().Be("CMMS");
        ev.AggregateId.Should().Be("WO-C");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-C");
        doc.RootElement.GetProperty("CompletedAt").GetDateTime().Should().Be(wo.CompletedAt!.Value);
        doc.RootElement.GetProperty("Remark").GetString().Should().Be("정상 완료");
    }

    [Fact]
    public void Cancel_raises_WorkOrderCancelled_event_with_outbox_envelope()
    {
        var wo = WorkOrder.Create("WO-X", "EQ-X", "PM", "점검", "tech01", Issued).Value;

        wo.Cancel().IsSuccess.Should().BeTrue();

        var ev = wo.DomainEvents.OfType<WorkOrderCancelledDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("WorkOrderCancelled");
        ev.Module.Should().Be("CMMS");
        ev.AggregateId.Should().Be("WO-X");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-X");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => WorkOrder.Restore("WO-R", null, "EQ-1", "PM", "점검", "tech01", Issued,
                Issued.AddMinutes(5), null, WorkOrderStatus.InProgress, null, null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.ClearDomainEvents();
        wo.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
