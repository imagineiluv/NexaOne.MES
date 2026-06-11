using NexaOne.Common;

namespace NexaOne.DLV.Domain;

public enum DeliveryOrderStatus
{
    Draft,
    Confirmed,
    Shipped,
    Cancelled
}

public sealed class DeliveryOrder : AuditableEntity<string>
{
    private readonly List<DeliveryItem> _items = [];

    private DeliveryOrder(string orderId) : base(orderId) { }

    public string CustomerName { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public DateTime RequestedDate { get; private set; }
    public DateTime? ShippedDate { get; private set; }
    public DeliveryOrderStatus Status { get; private set; }
    public string? Remark { get; private set; }
    public IReadOnlyList<DeliveryItem> Items => _items.AsReadOnly();
    public decimal TotalQty => _items.Sum(i => i.PlannedQty);

    public static Result<DeliveryOrder> Create(
        string orderId,
        string customerName,
        string plantId,
        DateTime requestedDate,
        string? remark = null)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return Result.Failure<DeliveryOrder>(Error.Validation(nameof(orderId), "Order ID is required."));
        if (string.IsNullOrWhiteSpace(customerName))
            return Result.Failure<DeliveryOrder>(Error.Validation(nameof(customerName), "Customer name is required."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<DeliveryOrder>(Error.Validation(nameof(plantId), "Plant ID is required."));

        var order = new DeliveryOrder(orderId)
        {
            CustomerName = customerName,
            PlantId = plantId,
            RequestedDate = requestedDate,
            Status = DeliveryOrderStatus.Draft,
            Remark = remark
        };
        return order;
    }

    public void AddItem(DeliveryItem item)
    {
        _items.Add(item);
        UpdatedAt = DateTime.UtcNow;
    }

    public Result Confirm()
    {
        if (Status != DeliveryOrderStatus.Draft)
            return Result.Failure(Error.Conflict("Delivery order can only be confirmed from Draft status."));

        Status = DeliveryOrderStatus.Confirmed;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Ship(DateTime shippedDate)
    {
        if (Status != DeliveryOrderStatus.Confirmed)
            return Result.Failure(Error.Conflict("Delivery order can only be shipped from Confirmed status."));

        Status = DeliveryOrderStatus.Shipped;
        ShippedDate = shippedDate;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == DeliveryOrderStatus.Shipped || Status == DeliveryOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Delivery order cannot be cancelled in its current status."));

        Status = DeliveryOrderStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
