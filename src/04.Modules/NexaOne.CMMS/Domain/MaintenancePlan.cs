using NexaOne.Common;

namespace NexaOne.CMMS.Domain;

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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 항상 Status=Planned로 강제하고, 기존 ToDomain은 Status에 따라 Start()/Complete()/Cancel()을
    /// 재생(replay)해 복원했다. 이제 그 전이가 도메인 이벤트를 발행하므로 읽기경로마다 phantom 이벤트가
    /// 발생하는 회귀를 막기 위해, Restore는 모든 영속 필드를 직접 채우고 어떤 이벤트도 발행하지 않는다.</summary>
    public static MaintenancePlan Restore(
        string planId, string planName, string equipmentId, string planType, string cycleType,
        DateTime scheduledDate, decimal estimatedDurationHours, string assigneeId, MaintenancePlanStatus status)
        => new(planId)
        {
            PlanName = planName,
            EquipmentId = equipmentId,
            PlanType = planType,
            CycleType = cycleType,
            ScheduledDate = scheduledDate,
            EstimatedDurationHours = estimatedDurationHours,
            AssigneeId = assigneeId,
            Status = status
        };

    public Result Start()
    {
        if (Status != MaintenancePlanStatus.Planned)
            return Result.Failure(Error.Conflict("Maintenance plan can only be started from Planned status."));

        Status = MaintenancePlanStatus.InProgress;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 착수를 도메인 이벤트로 발행한다. 리포가 착수(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new MaintenancePlanStartedDomainEvent(Id, EquipmentId, AssigneeId, ScheduledDate));
        return Result.Success();
    }

    public Result Complete()
    {
        if (Status != MaintenancePlanStatus.InProgress)
            return Result.Failure(Error.Conflict("Maintenance plan can only be completed from InProgress status."));

        Status = MaintenancePlanStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 완료를 도메인 이벤트로 발행한다. 리포가 완료(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new MaintenancePlanCompletedDomainEvent(Id, EquipmentId));
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == MaintenancePlanStatus.Completed || Status == MaintenancePlanStatus.Cancelled)
            return Result.Failure(Error.Conflict("Maintenance plan cannot be cancelled in its current status."));

        Status = MaintenancePlanStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 취소를 도메인 이벤트로 발행한다. 리포가 취소(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new MaintenancePlanCancelledDomainEvent(Id, EquipmentId));
        return Result.Success();
    }
}
