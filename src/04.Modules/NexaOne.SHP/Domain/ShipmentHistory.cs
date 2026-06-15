using NexaOne.Common;

namespace NexaOne.SHP.Domain;

public sealed class ShipmentHistory : AuditableEntity<string>
{
    private ShipmentHistory(string historyId) : base(historyId) { }

    public string DeliveryOrderId { get; private set; } = string.Empty;
    public DateTime ShippedAt { get; private set; }
    public decimal ShippedQty { get; private set; }
    public string ShippedBy { get; private set; } = string.Empty;
    public string? Carrier { get; private set; }
    public string? TrackingNo { get; private set; }

    public static Result<ShipmentHistory> Create(
        string historyId,
        string deliveryOrderId,
        DateTime shippedAt,
        decimal shippedQty,
        string shippedBy,
        string? carrier = null,
        string? trackingNo = null)
    {
        if (string.IsNullOrWhiteSpace(historyId))
            return Result.Failure<ShipmentHistory>(Error.Validation(nameof(historyId), "History ID is required."));
        if (string.IsNullOrWhiteSpace(deliveryOrderId))
            return Result.Failure<ShipmentHistory>(Error.Validation(nameof(deliveryOrderId), "Delivery order ID is required."));
        if (shippedQty <= 0)
            return Result.Failure<ShipmentHistory>(Error.Validation(nameof(shippedQty), "Shipped quantity must be positive."));
        if (string.IsNullOrWhiteSpace(shippedBy))
            return Result.Failure<ShipmentHistory>(Error.Validation(nameof(shippedBy), "Shipped by is required."));

        var history = new ShipmentHistory(historyId)
        {
            DeliveryOrderId = deliveryOrderId,
            ShippedAt = shippedAt,
            ShippedQty = shippedQty,
            ShippedBy = shippedBy,
            Carrier = carrier,
            TrackingNo = trackingNo
        };
        return history;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// 기존 ToDomain은 Create로 재구성해 감사 컬럼(CreatedBy/CreatedAt/UpdatedBy/UpdatedAt)과 소프트삭제
    /// 상태(IsDeleted/DeletedAt)를 읽기마다 유실·리셋했다(CreatedAt이 DateTime.UtcNow로 덮어써짐).
    /// Restore는 new(...) 직접 경로로 영속 필드를 그대로 복원해 읽기경로 상태손실을 막는다.</summary>
    public static ShipmentHistory Restore(
        string historyId, string deliveryOrderId, DateTime shippedAt, decimal shippedQty,
        string shippedBy, string? carrier, string? trackingNo,
        string createdBy, DateTime createdAt, string? updatedBy, DateTime? updatedAt)
        => new(historyId)
        {
            DeliveryOrderId = deliveryOrderId,
            ShippedAt = shippedAt,
            ShippedQty = shippedQty,
            ShippedBy = shippedBy,
            Carrier = carrier,
            TrackingNo = trackingNo,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            UpdatedBy = updatedBy,
            UpdatedAt = updatedAt
        };
}
