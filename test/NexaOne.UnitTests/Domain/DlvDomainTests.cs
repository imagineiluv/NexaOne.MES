using NexaOne.DLV.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class DlvDomainTests
{
    private static readonly DateTime ShippedAt = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

    // ── DeliveryItem ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_item_valid_succeeds()
    {
        var result = DeliveryItem.Create("ITEM001", "DO001", "PROD001", 100m);
        result.IsSuccess.Should().BeTrue();
        result.Value.ActualQty.Should().BeNull();
    }

    [Fact]
    public void Create_item_with_lot_id_succeeds()
    {
        var result = DeliveryItem.Create("ITEM001", "DO001", "PROD001", 100m, "LOT001");
        result.IsSuccess.Should().BeTrue();
        result.Value.LotId.Should().Be("LOT001");
    }

    [Fact]
    public void Create_item_zero_qty_fails()
    {
        var result = DeliveryItem.Create("ITEM001", "DO001", "PROD001", 0m);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_item_negative_qty_fails()
    {
        var result = DeliveryItem.Create("ITEM001", "DO001", "PROD001", -5m);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SetActualQty_sets_value_successfully()
    {
        var item = DeliveryItem.Create("ITEM001", "DO001", "PROD001", 100m).Value;
        var result = item.SetActualQty(95m);
        result.IsSuccess.Should().BeTrue();
        item.ActualQty.Should().Be(95m);
    }

    [Fact]
    public void SetActualQty_zero_is_allowed()
    {
        var item = DeliveryItem.Create("ITEM001", "DO001", "PROD001", 100m).Value;
        var result = item.SetActualQty(0m);
        result.IsSuccess.Should().BeTrue();
        item.ActualQty.Should().Be(0m);
    }

    [Fact]
    public void SetActualQty_negative_fails()
    {
        var item = DeliveryItem.Create("ITEM001", "DO001", "PROD001", 100m).Value;
        var result = item.SetActualQty(-1m);
        result.IsFailure.Should().BeTrue();
    }

    // ── ShipmentHistory ───────────────────────────────────────────────────────

    [Fact]
    public void Create_shipment_history_valid_succeeds()
    {
        var result = ShipmentHistory.Create("SH001", "DO001", ShippedAt, 100m, "user01", "CJ대한통운", "1234567890");
        result.IsSuccess.Should().BeTrue();
        result.Value.Carrier.Should().Be("CJ대한통운");
        result.Value.TrackingNo.Should().Be("1234567890");
    }

    [Fact]
    public void Create_shipment_history_without_optional_fields_succeeds()
    {
        var result = ShipmentHistory.Create("SH001", "DO001", ShippedAt, 50m, "user01");
        result.IsSuccess.Should().BeTrue();
        result.Value.Carrier.Should().BeNull();
    }

    [Fact]
    public void Create_shipment_history_zero_qty_fails()
    {
        var result = ShipmentHistory.Create("SH001", "DO001", ShippedAt, 0m, "user01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_shipment_history_missing_shipped_by_fails()
    {
        var result = ShipmentHistory.Create("SH001", "DO001", ShippedAt, 100m, "");
        result.IsFailure.Should().BeTrue();
    }
}
