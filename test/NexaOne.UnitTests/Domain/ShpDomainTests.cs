using NexaOne.SHP.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class ShpDomainTests
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

    // 읽기경로 상태손실 회귀 방지: 기존 ToDomain(Create 경로)은 감사 컬럼을 읽기마다 유실/리셋했다.
    // Restore가 영속 감사 상태(CreatedBy/CreatedAt/UpdatedBy/UpdatedAt)를 그대로 복원하는지 검증한다.
    [Fact]
    public void Restore_preserves_persisted_audit_fields()
    {
        var createdAt = new DateTime(2026, 7, 19, 8, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 7, 20, 9, 30, 0, DateTimeKind.Utc);

        var history = ShipmentHistory.Restore(
            "SH001", "DO001", ShippedAt, 100m, "user01", "CJ대한통운", "1234567890",
            createdBy: "creator01", createdAt: createdAt, updatedBy: "editor02", updatedAt: updatedAt);

        history.CreatedBy.Should().Be("creator01", "감사 생성자는 읽기경로에서 빈 문자열로 리셋되면 안 된다");
        history.CreatedAt.Should().Be(createdAt, "CreatedAt이 읽기 시 DateTime.UtcNow로 덮어써지면 안 된다");
        history.UpdatedBy.Should().Be("editor02");
        history.UpdatedAt.Should().Be(updatedAt);
        // 비즈니스 필드도 함께 복원된다.
        history.DeliveryOrderId.Should().Be("DO001");
        history.Carrier.Should().Be("CJ대한통운");
    }
}
