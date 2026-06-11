using NexaOne.Common;

namespace NexaOne.EMS.Domain;

public enum WorkOrderStatus
{
    Issued,
    InProgress,
    Completed,
    Cancelled
}

public sealed class WorkOrder : AuditableEntity<string>
{
    private static readonly HashSet<string> ValidWoTypes = ["PM", "CM"];

    private WorkOrder(string woId) : base(woId) { }

    public string? PlanId { get; private set; }
    public string EquipmentId { get; private set; } = string.Empty;
    public string WoType { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string AssigneeId { get; private set; } = string.Empty;
    public DateTime IssuedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public WorkOrderStatus Status { get; private set; }
    public string? FailureCodeId { get; private set; }
    public string? Remark { get; private set; }

    public static Result<WorkOrder> Create(
        string woId,
        string equipmentId,
        string woType,
        string description,
        string assigneeId,
        DateTime issuedAt,
        string? planId = null,
        string? failureCodeId = null)
    {
        if (string.IsNullOrWhiteSpace(woId))
            return Result.Failure<WorkOrder>(Error.Validation(nameof(woId), "Work order ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<WorkOrder>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (!ValidWoTypes.Contains(woType))
            return Result.Failure<WorkOrder>(Error.Validation(nameof(woType), "Work order type must be 'PM' or 'CM'."));
        if (string.IsNullOrWhiteSpace(description))
            return Result.Failure<WorkOrder>(Error.Validation(nameof(description), "Description is required."));
        if (string.IsNullOrWhiteSpace(assigneeId))
            return Result.Failure<WorkOrder>(Error.Validation(nameof(assigneeId), "Assignee ID is required."));

        var wo = new WorkOrder(woId)
        {
            PlanId = planId,
            EquipmentId = equipmentId,
            WoType = woType,
            Description = description,
            AssigneeId = assigneeId,
            IssuedAt = issuedAt,
            FailureCodeId = failureCodeId,
            Status = WorkOrderStatus.Issued
        };
        return wo;
    }

    public Result Start()
    {
        if (Status != WorkOrderStatus.Issued)
            return Result.Failure(Error.Conflict("Work order can only be started from Issued status."));

        Status = WorkOrderStatus.InProgress;
        StartedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Complete(string? remark = null)
    {
        if (Status != WorkOrderStatus.InProgress)
            return Result.Failure(Error.Conflict("Work order can only be completed from InProgress status."));

        Status = WorkOrderStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Remark = remark;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == WorkOrderStatus.Completed || Status == WorkOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Work order cannot be cancelled in its current status."));

        Status = WorkOrderStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
