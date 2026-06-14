using System.Text.Json;
using NexaOne.SHP.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class DeliveryOrderTests
{
    private static readonly DateTime Requested = new(2026, 3, 15);

    private static DeliveryOrder Draft() =>
        DeliveryOrder.Create("DO001", "Customer A", "P01", Requested).Value;

    [Fact]
    public void Create_order_starts_in_Draft()
    {
        var o = Draft();
        o.Status.Should().Be(DeliveryOrderStatus.Draft);
        o.CustomerName.Should().Be("Customer A");
        o.ShippedDate.Should().BeNull();
    }

    [Fact]
    public void Create_with_empty_customer_fails()
    {
        DeliveryOrder.Create("DO002", "", "P01", Requested).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_with_empty_plant_fails()
    {
        DeliveryOrder.Create("DO003", "Customer A", "", Requested).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Confirm_moves_to_Confirmed()
    {
        var o = Draft();
        o.Confirm().IsSuccess.Should().BeTrue();
        o.Status.Should().Be(DeliveryOrderStatus.Confirmed);
    }

    [Fact]
    public void Confirm_from_non_Draft_fails()
    {
        var o = Draft();
        o.Confirm();
        o.Confirm().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Ship_requires_Confirmed()
    {
        var o = Draft();
        o.Ship(Requested).IsFailure.Should().BeTrue();
        o.Confirm();
        o.Ship(Requested).IsSuccess.Should().BeTrue();
        o.Status.Should().Be(DeliveryOrderStatus.Shipped);
        o.ShippedDate.Should().Be(Requested);
    }

    [Fact]
    public void Cancel_from_Shipped_fails()
    {
        var o = Draft();
        o.Confirm();
        o.Ship(Requested);
        o.Cancel().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Draft_succeeds()
    {
        var o = Draft();
        o.Cancel().IsSuccess.Should().BeTrue();
        o.Status.Should().Be(DeliveryOrderStatus.Cancelled);
    }

    [Fact]
    public void TotalQty_sums_item_quantities()
    {
        var o = Draft();
        o.AddItem(DeliveryItem.Create("ITEM-1", "DO001", "PROD-A", 100m).Value);
        o.AddItem(DeliveryItem.Create("ITEM-2", "DO001", "PROD-B", 200m).Value);
        o.TotalQty.Should().Be(300m);
    }

    // ── ADR-002: 상태 전이 시 도메인 이벤트 발행(opt-in outbox) ─────────────────────────────

    [Fact]
    public void Confirm_raises_DeliveryOrderConfirmed_event_with_outbox_envelope()
    {
        var o = Draft();

        o.Confirm();

        var ev = o.DomainEvents.OfType<DeliveryOrderConfirmedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("DeliveryOrderConfirmed");
        ev.Module.Should().Be("SHP");
        ev.AggregateId.Should().Be("DO001", "주문별 순서 보장을 위해 AGGREGATE_ID는 OrderId여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public void Ship_raises_DeliveryOrderShipped_event_with_shipped_date()
    {
        var shipped = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var o = Draft();
        o.Confirm();
        o.ClearDomainEvents();   // 확정 이벤트 제거 — 출하 이벤트만 검증

        o.Ship(shipped);

        var ev = o.DomainEvents.OfType<DeliveryOrderShippedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("DeliveryOrderShipped");
        ev.Module.Should().Be("SHP");
        ev.AggregateId.Should().Be("DO001");
        ev.ShippedDate.Should().Be(shipped);

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("Shipped");
        doc.RootElement.GetProperty("ShippedDate").GetDateTime().Should().Be(shipped);
    }

    [Fact]
    public void Cancel_raises_DeliveryOrderCancelled_event_with_outbox_envelope()
    {
        var o = Draft();

        o.Cancel();

        var ev = o.DomainEvents.OfType<DeliveryOrderCancelledDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("DeliveryOrderCancelled");
        ev.Module.Should().Be("SHP");
        ev.AggregateId.Should().Be("DO001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("Cancelled");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => DeliveryOrder.Restore("DO009", "Customer A", "P01", Requested,
                DeliveryOrderStatus.Confirmed, null, null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var o = Draft();
        o.Confirm();
        o.ClearDomainEvents();
        o.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
