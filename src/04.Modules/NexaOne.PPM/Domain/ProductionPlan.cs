using NexaOne.Common;

namespace NexaOne.PPM.Domain;

public enum ProductionPlanStatus
{
    Draft,
    Released,
    InProgress,
    Completed,
    Cancelled
}

public sealed class ProductionPlan : AuditableEntity<string>
{
    private ProductionPlan(string planId) : base(planId) { }

    public string PlanName { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public string ProductId { get; private set; } = string.Empty;
    public decimal PlannedQty { get; private set; }
    public DateTime PlannedStartDate { get; private set; }
    public DateTime PlannedEndDate { get; private set; }
    public ProductionPlanStatus Status { get; private set; }
    public string? Remark { get; private set; }

    public static Result<ProductionPlan> Create(
        string planId,
        string planName,
        string plantId,
        string productId,
        decimal plannedQty,
        DateTime plannedStartDate,
        DateTime plannedEndDate,
        string? remark = null)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return Result.Failure<ProductionPlan>(Error.Validation(nameof(planId), "Plan ID is required."));
        if (string.IsNullOrWhiteSpace(planName))
            return Result.Failure<ProductionPlan>(Error.Validation(nameof(planName), "Plan name is required."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<ProductionPlan>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<ProductionPlan>(Error.Validation(nameof(productId), "Product ID is required."));
        if (plannedQty <= 0)
            return Result.Failure<ProductionPlan>(Error.Validation(nameof(plannedQty), "Planned quantity must be positive."));
        if (plannedStartDate > plannedEndDate)
            return Result.Failure<ProductionPlan>(Error.Validation(nameof(plannedStartDate), "Planned start date must be on or before planned end date."));

        var plan = new ProductionPlan(planId)
        {
            PlanName = planName,
            PlantId = plantId,
            ProductId = productId,
            PlannedQty = plannedQty,
            PlannedStartDate = plannedStartDate,
            PlannedEndDate = plannedEndDate,
            Status = ProductionPlanStatus.Draft,
            Remark = remark
        };
        return plan;
    }

    public Result Release()
    {
        if (Status != ProductionPlanStatus.Draft)
            return Result.Failure(Error.Conflict("Production plan can only be released from Draft status."));

        Status = ProductionPlanStatus.Released;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Start()
    {
        if (Status != ProductionPlanStatus.Released)
            return Result.Failure(Error.Conflict("Production plan can only be started from Released status."));

        Status = ProductionPlanStatus.InProgress;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Complete()
    {
        if (Status != ProductionPlanStatus.InProgress)
            return Result.Failure(Error.Conflict("Production plan can only be completed from InProgress status."));

        Status = ProductionPlanStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == ProductionPlanStatus.Completed || Status == ProductionPlanStatus.Cancelled)
            return Result.Failure(Error.Conflict("Production plan cannot be cancelled in its current status."));

        Status = ProductionPlanStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
