using NexaOne.Common;

namespace NexaOne.DLV.Domain;

public sealed class DeliveryItem : AuditableEntity<string>
{
    private DeliveryItem(string itemId) : base(itemId) { }

    public string DeliveryOrderId { get; private set; } = string.Empty;
    public string ProductId { get; private set; } = string.Empty;
    public decimal PlannedQty { get; private set; }
    public decimal? ActualQty { get; private set; }
    public string? LotId { get; private set; }

    public static Result<DeliveryItem> Create(
        string itemId,
        string deliveryOrderId,
        string productId,
        decimal plannedQty,
        string? lotId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(itemId), "Item ID is required."));
        if (string.IsNullOrWhiteSpace(deliveryOrderId))
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(deliveryOrderId), "Delivery order ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(productId), "Product ID is required."));
        if (plannedQty <= 0)
            return Result.Failure<DeliveryItem>(Error.Validation(nameof(plannedQty), "Planned quantity must be positive."));

        var item = new DeliveryItem(itemId)
        {
            DeliveryOrderId = deliveryOrderId,
            ProductId = productId,
            PlannedQty = plannedQty,
            LotId = lotId
        };
        return item;
    }

    public Result SetActualQty(decimal qty)
    {
        if (qty < 0)
            return Result.Failure(Error.Validation(nameof(qty), "Actual quantity must be non-negative."));

        ActualQty = qty;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
