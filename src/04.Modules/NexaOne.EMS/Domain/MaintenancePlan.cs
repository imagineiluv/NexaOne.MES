using NexaOne.Common;

namespace NexaOne.EMS.Domain;

public enum MaintenancePlanStatus
{
    Planned,
    InProgress,
    Completed,
    Cancelled
}

public sealed class MaintenancePlan : AuditableEntity<string>
{
    private static readonly HashSet<string> ValidPlanTypes = ["PM", "CM"];
    private static readonly HashSet<string> ValidCycleTypes = ["Daily", "Weekly", "Monthly", "Yearly"];

    private MaintenancePlan(string planId) : base(planId) { }

    public string PlanName { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string PlanType { get; private set; } = string.Empty;
    public string CycleType { get; private set; } = string.Empty;
    public DateTime ScheduledDate { get; private set; }
    public decimal EstimatedDurationHours { get; private set; }
    public string AssigneeId { get; private set; } = string.Empty;
    public MaintenancePlanStatus Status { get; private set; }

    public static Result<MaintenancePlan> Create(
        string planId,
        string planName,
        string equipmentId,
        string planType,
        string cycleType,
        DateTime scheduledDate,
        decimal estimatedDurationHours,
        string assigneeId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(planId), "Plan ID is required."));
        if (string.IsNullOrWhiteSpace(planName))
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(planName), "Plan name is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (!ValidPlanTypes.Contains(planType))
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(planType), "Plan type must be 'PM' or 'CM'."));
        if (!ValidCycleTypes.Contains(cycleType))
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(cycleType), "Cycle type must be 'Daily', 'Weekly', 'Monthly', or 'Yearly'."));
        if (estimatedDurationHours <= 0)
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(estimatedDurationHours), "Estimated duration must be positive."));
        if (string.IsNullOrWhiteSpace(assigneeId))
            return Result.Failure<MaintenancePlan>(Error.Validation(nameof(assigneeId), "Assignee ID is required."));

        var plan = new MaintenancePlan(planId)
        {
            PlanName = planName,
            EquipmentId = equipmentId,
            PlanType = planType,
            CycleType = cycleType,
            ScheduledDate = scheduledDate,
            EstimatedDurationHours = estimatedDurationHours,
            AssigneeId = assigneeId,
            Status = MaintenancePlanStatus.Planned
        };
        return plan;
    }

    public Result Start()
    {
        if (Status != MaintenancePlanStatus.Planned)
            return Result.Failure(Error.Conflict("Maintenance plan can only be started from Planned status."));

        Status = MaintenancePlanStatus.InProgress;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Complete()
    {
        if (Status != MaintenancePlanStatus.InProgress)
            return Result.Failure(Error.Conflict("Maintenance plan can only be completed from InProgress status."));

        Status = MaintenancePlanStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == MaintenancePlanStatus.Completed || Status == MaintenancePlanStatus.Cancelled)
            return Result.Failure(Error.Conflict("Maintenance plan cannot be cancelled in its current status."));

        Status = MaintenancePlanStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
