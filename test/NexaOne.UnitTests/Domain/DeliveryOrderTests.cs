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
}
