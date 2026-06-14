using System.Text.Json;
using NexaOne.POM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class ProductionOrderTests
{
    private static readonly DateTime SchedStart = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SchedEnd   = new(2026, 3, 1, 18, 0, 0, DateTimeKind.Utc);

    private static ProductionOrder Issued() =>
        ProductionOrder.Create("ORD001", "PLAN001", "EQ001", "PROD01", 100m, SchedStart, SchedEnd).Value;

    [Fact]
    public void Create_order_starts_in_Issued()
    {
        var o = Issued();
        o.Status.Should().Be(ProductionOrderStatus.Issued);
        o.OrderQty.Should().Be(100m);
        o.ActualQty.Should().BeNull();
        o.ActualStart.Should().BeNull();
        o.ActualEnd.Should().BeNull();
    }

    [Fact]
    public void Create_with_zero_qty_fails()
    {
        ProductionOrder.Create("ORD001", "PLAN001", "EQ001", "PROD01", 0, SchedStart, SchedEnd)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_with_start_after_end_fails()
    {
        ProductionOrder.Create("ORD001", "PLAN001", "EQ001", "PROD01", 100m, SchedEnd, SchedStart)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_from_Issued_moves_to_InProgress()
    {
        var o = Issued();
        var actualStart = DateTime.UtcNow;
        o.Start(actualStart).IsSuccess.Should().BeTrue();
        o.Status.Should().Be(ProductionOrderStatus.InProgress);
        o.ActualStart.Should().Be(actualStart);
    }

    [Fact]
    public void Start_from_non_Issued_fails()
    {
        var o = Issued();
        o.Cancel();
        o.Start(DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Complete_from_InProgress_succeeds()
    {
        var o = Issued();
        o.Start(DateTime.UtcNow);
        var actualEnd = DateTime.UtcNow;
        o.Complete(95m, actualEnd).IsSuccess.Should().BeTrue();
        o.Status.Should().Be(ProductionOrderStatus.Completed);
        o.ActualQty.Should().Be(95m);
        o.ActualEnd.Should().Be(actualEnd);
    }

    [Fact]
    public void Complete_from_Issued_fails()
    {
        var o = Issued();
        o.Complete(100m, DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Complete_with_negative_qty_fails()
    {
        var o = Issued();
        o.Start(DateTime.UtcNow);
        o.Complete(-1m, DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Issued_succeeds()
    {
        var o = Issued();
        o.Cancel().IsSuccess.Should().BeTrue();
        o.Status.Should().Be(ProductionOrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_from_Completed_fails()
    {
        var o = Issued();
        o.Start(DateTime.UtcNow);
        o.Complete(100m, DateTime.UtcNow);
        o.Cancel().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_already_Cancelled_fails()
    {
        var o = Issued();
        o.Cancel();
        o.Cancel().IsFailure.Should().BeTrue();
    }
}

/// <summary>ProductionOrder 애그리거트 — 착수/완료/취소 시 도메인 이벤트 발행(ADR-002 POM 슬라이스).
/// Restore(읽기경로)는 이벤트를 발행하지 않아야 하며(유령 이벤트 방지), 발행 후 ClearDomainEvents로 비워져야 한다.</summary>
public sealed class ProductionOrderOutboxTests
{
    private static readonly DateTime Sched = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static ProductionOrder Issued(string id = "ORD-OB") =>
        ProductionOrder.Create(id, "PLAN001", "EQ001", "PROD01", 100m, Sched, Sched.AddHours(10)).Value;

    [Fact]
    public void Start_raises_ProductionOrderStarted_event_with_outbox_envelope()
    {
        var order = Issued("PO-START");
        var actualStart = Sched.AddMinutes(5);

        order.Start(actualStart).IsSuccess.Should().BeTrue();

        var ev = order.DomainEvents.OfType<ProductionOrderStartedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("ProductionOrderStarted");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("PO-START", "오더별 순서 보장을 위해 AGGREGATE_ID는 OrderId(ORDER_ID)여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ001");
        doc.RootElement.GetProperty("ProductId").GetString().Should().Be("PROD01");
        doc.RootElement.GetProperty("ActualStart").GetDateTime().Should().Be(actualStart);
    }

    [Fact]
    public void Complete_raises_ProductionOrderCompleted_event_with_outbox_envelope()
    {
        var order = Issued("PO-DONE");
        order.Start(Sched.AddMinutes(5));
        order.ClearDomainEvents();   // 착수 이벤트 제거 — 완료 이벤트만 검증
        var actualEnd = Sched.AddHours(9);

        order.Complete(95m, actualEnd).IsSuccess.Should().BeTrue();

        var ev = order.DomainEvents.OfType<ProductionOrderCompletedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("ProductionOrderCompleted");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("PO-DONE");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("ActualQty").GetDecimal().Should().Be(95m);
        doc.RootElement.GetProperty("ActualEnd").GetDateTime().Should().Be(actualEnd);
    }

    [Fact]
    public void Cancel_raises_ProductionOrderCancelled_event_with_order_id_payload()
    {
        var order = Issued("PO-CANCEL");

        order.Cancel().IsSuccess.Should().BeTrue();

        var ev = order.DomainEvents.OfType<ProductionOrderCancelledDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("ProductionOrderCancelled");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("PO-CANCEL");
        ev.Payload.Should().Be("PO-CANCEL", "취소 이벤트 Payload는 단일 값(ORDER_ID)이라 평문 문자열이다");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => ProductionOrder.Restore("PO-R", "PLAN001", "EQ001", "PROD01", 100m, 95m,
                Sched, Sched.AddHours(10), Sched.AddMinutes(5), Sched.AddHours(9), ProductionOrderStatus.Completed)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var order = Issued();
        order.Start(Sched.AddMinutes(5));
        order.ClearDomainEvents();
        order.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
