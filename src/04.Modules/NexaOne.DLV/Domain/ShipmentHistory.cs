using NexaOne.Common;

namespace NexaOne.DLV.Domain;

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
}
