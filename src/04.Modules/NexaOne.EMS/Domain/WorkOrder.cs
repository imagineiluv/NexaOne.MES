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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 항상 Issued로 강제하므로 진행/완료/취소된 작업지시가 Issued로 잘못 읽히는 것을 막는다.</summary>
    public static WorkOrder Restore(
        string woId, string? planId, string equipmentId, string woType, string description,
        string assigneeId, DateTime issuedAt, DateTime? startedAt, DateTime? completedAt,
        WorkOrderStatus status, string? failureCodeId, string? remark,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var wo = new WorkOrder(woId)
        {
            PlanId = planId,
            EquipmentId = equipmentId,
            WoType = woType,
            Description = description,
            AssigneeId = assigneeId,
            IssuedAt = issuedAt,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Status = status,
            FailureCodeId = failureCodeId,
            Remark = remark
        };
        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 그대로 복원해 매 읽기마다
        // CreatedAt=UtcNow 재생성·CreatedBy=""·UpdatedBy/At=null 리셋되는 상태손실을 막는다.
        wo.RestoreAudit(createdBy ?? wo.CreatedBy, createdAt ?? wo.CreatedAt, updatedBy, updatedAt);
        return wo;
    }

    public Result Start()
    {
        if (Status != WorkOrderStatus.Issued)
            return Result.Failure(Error.Conflict("Work order can only be started from Issued status."));

        Status = WorkOrderStatus.InProgress;
        StartedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 작업지시 착수를 도메인 이벤트로 발행한다. 리포가 착수(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new WorkOrderStartedDomainEvent(Id, EquipmentId, AssigneeId, StartedAt.Value));
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
        // ADR-002: 작업지시 완료를 도메인 이벤트로 발행한다. 리포가 완료(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new WorkOrderCompletedDomainEvent(Id, EquipmentId, CompletedAt.Value, FailureCodeId, Remark));
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == WorkOrderStatus.Completed || Status == WorkOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("Work order cannot be cancelled in its current status."));

        Status = WorkOrderStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        // ADR-002: 작업지시 취소를 도메인 이벤트로 발행한다. 리포가 취소(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new WorkOrderCancelledDomainEvent(Id, EquipmentId));
        return Result.Success();
    }
}
