using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>인터락 규칙 발동 이력 (FDC_INTERLOCK_HISTORY, design 10.4.1).
/// 발동 시 1행 생성하고, 해제 시 <see cref="Resolve"/>로 RESOLVED_AT/IS_RESOLVED를 갱신한다.</summary>
public sealed class FdcInterlockHistory : AuditableEntity<string>
{
    private FdcInterlockHistory(string historyId) : base(historyId) { }

    public string RuleId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string ParameterId { get; private set; } = string.Empty;
    public decimal TriggerValue { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public DateTime TriggeredAt { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public bool IsResolved { get; private set; }

    public static Result<FdcInterlockHistory> Create(
        string historyId,
        string ruleId,
        string equipmentId,
        string parameterId,
        decimal triggerValue,
        string action,
        string message,
        DateTime triggeredAt)
    {
        if (string.IsNullOrWhiteSpace(historyId))
            return Result.Failure<FdcInterlockHistory>(Error.Validation(nameof(historyId), "History ID is required."));
        if (string.IsNullOrWhiteSpace(ruleId))
            return Result.Failure<FdcInterlockHistory>(Error.Validation(nameof(ruleId), "Rule ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcInterlockHistory>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(parameterId))
            return Result.Failure<FdcInterlockHistory>(Error.Validation(nameof(parameterId), "Parameter ID is required."));
        if (string.IsNullOrWhiteSpace(action))
            return Result.Failure<FdcInterlockHistory>(Error.Validation(nameof(action), "Action is required."));

        var history = new FdcInterlockHistory(historyId)
        {
            RuleId = ruleId,
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            TriggerValue = triggerValue,
            Action = action,
            Message = message ?? string.Empty,
            TriggeredAt = triggeredAt,
            IsResolved = false
        };
        // ADR-002: 인터락 발동을 도메인 이벤트로 발행한다. 리포가 발동 이력과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        history.RaiseDomainEvent(new FdcInterlockTriggeredDomainEvent(
            historyId, ruleId, equipmentId, parameterId, action, history.Message, triggerValue));
        return history;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create+Resolve 재생은 읽기경로마다 PHANTOM FdcInterlockResolved 이벤트를 발행하므로, 전이 메서드 재생 없이
    /// 객체 초기화로 영속 상태(ResolvedAt/IsResolved 포함)를 직접 복원한다.</summary>
    public static FdcInterlockHistory Restore(
        string historyId, string ruleId, string equipmentId, string parameterId, decimal triggerValue,
        string action, string message, DateTime triggeredAt, DateTime? resolvedAt, bool isResolved,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var history = new FdcInterlockHistory(historyId)
        {
            RuleId = ruleId,
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            TriggerValue = triggerValue,
            Action = action,
            Message = message,
            TriggeredAt = triggeredAt,
            ResolvedAt = resolvedAt,
            IsResolved = isResolved
        };
        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 그대로 복원(미복원 시 CreatedAt이 매 읽기 UtcNow로 재생성·CreatedBy 리셋).
        history.RestoreAudit(createdBy ?? history.CreatedBy, createdAt ?? history.CreatedAt, updatedBy, updatedAt);
        return history;
    }

    /// <summary>인터락 해제 — 해제 시각을 기록하고 IS_RESOLVED를 true로 한다. (멱등)</summary>
    public void Resolve(DateTime resolvedAt, decimal? resolvedValue = null)
    {
        if (IsResolved) return;
        ResolvedAt = resolvedAt;
        IsResolved = true;
        // ADR-002: 인터락 해제를 도메인 이벤트로 발행한다. 리포가 해제(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new FdcInterlockResolvedDomainEvent(
            Id, RuleId, EquipmentId, ParameterId, resolvedValue ?? TriggerValue, resolvedAt));
    }
}
