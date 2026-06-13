using NexaOne.Common;

namespace NexaOne.POM.Domain;

public enum ProductionOrderStatus
{
    Issued,
    InProgress,
    Completed,
    Cancelled
}

public sealed class ProductionOrder : AuditableEntity<string>
{
    private ProductionOrder(string orderId) : base(orderId) { }

    public string PlanId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string ProductId { get; private set; } = string.Empty;
    public decimal OrderQty { get; private set; }
    public decimal? ActualQty { get; private set; }
    public DateTime ScheduledStart { get; private set; }
    public DateTime ScheduledEnd { get; private set; }
    public DateTime? ActualStart { get; private set; }
    public DateTime? ActualEnd { get; private set; }
    public ProductionOrderStatus Status { get; private set; }

    public static Result<ProductionOrder> Create(
        string orderId,
        string planId,
        string equipmentId,
        string productId,
        decimal orderQty,
        DateTime scheduledStart,
        DateTime scheduledEnd)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(orderId), "Order ID is required."));
        if (string.IsNullOrWhiteSpace(planId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(planId), "Plan ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(productId), "Product ID is required."));
        if (orderQty <= 0)
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(orderQty), "Order quantity must be positive."));
        if (scheduledStart > scheduledEnd)
            return Result.Failure<ProductionOrder>(Error.Validation(nameof(scheduledStart), "Scheduled start must be on or before scheduled end."));

        var order = new ProductionOrder(orderId)
        {
            PlanId = planId,
            EquipmentId = equipmentId,
            ProductId = productId,
            OrderQty = orderQty,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
            Status = ProductionOrderStatus.Issued
        };
        return order;
    }

    public Result Start(DateTime actualStart)
    {
        if (Status != ProductionOrderStatus.Issued)
            return Result.Failure(Error.Conflict("Production order can only be started from Issued status."));

        Status = ProductionOrderStatus.InProgress;
        ActualStart = actualStart;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Complete(decimal actualQty, DateTime actualEnd)
    {
        if (Status != ProductionOrderStatus.InProgress)
            return Result.Failure(Error.Conflict("Production order can only be completed from InProgress status."));
        if (actualQty < 0)
            return Result.Failure(Error.Validation(nameof(actualQty), "Actual quantity must be non-negative."));

        Status = ProductionOrderStatus.Completed;
        ActualQty = actualQty;
        ActualEnd = actualEnd;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == ProductionOrderStatus.Completed || Status == ProductionOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Production order cannot be cancelled in its current status."));

        Status = ProductionOrderStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
