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
    public FdcInterlockEffectState EffectState { get; private set; }
    public string? ApplyAcknowledgementId { get; private set; }
    public DateTime? ApplyConfirmedAt { get; private set; }
    public DateTime? ConditionNormalizedAt { get; private set; }
    public decimal? ConditionNormalizedValue { get; private set; }
    public string? ReleaseAcknowledgementId { get; private set; }
    public DateTime? ReleaseConfirmedAt { get; private set; }
    public string? LastError { get; private set; }
    public int Version { get; private set; }

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
            IsResolved = false,
            EffectState = FdcInterlockEffectState.Prepared,
            Version = 1
        };
        // ADR-002: 인터락 발동을 도메인 이벤트로 발행한다. 리포가 발동 이력과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        history.RaiseDomainEvent(new FdcInterlockTriggeredDomainEvent(
            historyId, ruleId, equipmentId, parameterId, action, history.Message, triggerValue));
        return history;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원하고 lifecycle/ack 증거 불변식을 검증한다. 리포지토리 읽기 전용 —
    /// Create+Resolve 재생은 읽기경로마다 PHANTOM FdcInterlockResolved 이벤트를 발행하므로, 전이 메서드 재생 없이
    /// 객체 초기화로 영속 상태(ResolvedAt/IsResolved 포함)를 직접 복원한다.</summary>
    public static FdcInterlockHistory Restore(
        string historyId, string ruleId, string equipmentId, string parameterId, decimal triggerValue,
        string action, string message, DateTime triggeredAt, DateTime? resolvedAt, bool isResolved,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null,
        FdcInterlockEffectState effectState = FdcInterlockEffectState.Prepared,
        string? applyAcknowledgementId = null, DateTime? applyConfirmedAt = null,
        DateTime? conditionNormalizedAt = null, decimal? conditionNormalizedValue = null,
        string? releaseAcknowledgementId = null, DateTime? releaseConfirmedAt = null,
        string? lastError = null, int version = 1)
    {
        if (!Enum.IsDefined(effectState))
            throw new InvalidOperationException($"Unknown FDC interlock effect state '{effectState}'.");
        if (version <= 0)
            throw new InvalidOperationException("FDC interlock effect version must be positive.");
        if (isResolved != (effectState == FdcInterlockEffectState.Resolved))
            throw new InvalidOperationException(
                $"FDC interlock effect '{historyId}' has inconsistent resolved/state values.");
        var isLegacyResolved = isResolved
                               && string.Equals(lastError, "LegacyResolvedBeforeV146", StringComparison.Ordinal);
        if (effectState >= FdcInterlockEffectState.Applied
            && !isLegacyResolved
            && (string.IsNullOrWhiteSpace(applyAcknowledgementId) || applyConfirmedAt is null))
            throw new InvalidOperationException(
                $"FDC interlock effect '{historyId}' has no confirmed apply evidence for state '{effectState}'.");
        if (effectState >= FdcInterlockEffectState.ConditionNormalized
            && !isLegacyResolved
            && (conditionNormalizedAt is null || conditionNormalizedValue is null))
            throw new InvalidOperationException(
                $"FDC interlock effect '{historyId}' has no condition-normalized evidence for state '{effectState}'.");
        if (effectState == FdcInterlockEffectState.Resolved
            && !isLegacyResolved
            && (string.IsNullOrWhiteSpace(releaseAcknowledgementId)
                || releaseConfirmedAt is null
                || resolvedAt is null))
            throw new InvalidOperationException(
                $"FDC interlock effect '{historyId}' has no confirmed release evidence.");

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
            IsResolved = isResolved,
            EffectState = effectState,
            ApplyAcknowledgementId = applyAcknowledgementId,
            ApplyConfirmedAt = applyConfirmedAt,
            ConditionNormalizedAt = conditionNormalizedAt,
            ConditionNormalizedValue = conditionNormalizedValue,
            ReleaseAcknowledgementId = releaseAcknowledgementId,
            ReleaseConfirmedAt = releaseConfirmedAt,
            LastError = lastError,
            Version = version
        };
        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 그대로 복원(미복원 시 CreatedAt이 매 읽기 UtcNow로 재생성·CreatedBy 리셋).
        history.RestoreAudit(createdBy ?? history.CreatedBy, createdAt ?? history.CreatedAt, updatedBy, updatedAt);
        return history;
    }

    /// <summary>인터락 해제 — 해제 시각을 기록하고 IS_RESOLVED를 true로 한다. (멱등)</summary>
    internal void Resolve(DateTime resolvedAt, decimal? resolvedValue = null)
    {
        if (IsResolved) return;
        if (EffectState is not FdcInterlockEffectState.ConditionNormalized
            and not FdcInterlockEffectState.ReleasePending)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot resolve from state '{EffectState}'.");
        if (string.IsNullOrWhiteSpace(ReleaseAcknowledgementId) || ReleaseConfirmedAt is null)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot resolve without confirmed release evidence.");
        if (resolvedAt < ReleaseConfirmedAt.Value)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot resolve before the physical release confirmation.");
        ResolvedAt = resolvedAt;
        IsResolved = true;
        EffectState = FdcInterlockEffectState.Resolved;
        Version++;
        // ADR-002: 인터락 해제를 도메인 이벤트로 발행한다. 리포가 해제(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new FdcInterlockResolvedDomainEvent(
            Id, RuleId, EquipmentId, ParameterId, resolvedValue ?? TriggerValue, resolvedAt));
    }


    internal void MarkApplied(string? acknowledgementId, DateTime confirmedAt)
    {
        if (IsResolved)
            throw new InvalidOperationException($"Resolved effect '{Id}' cannot be marked applied.");
        if (string.IsNullOrWhiteSpace(acknowledgementId))
            throw new ArgumentException("Apply acknowledgement ID is required.", nameof(acknowledgementId));
        ApplyAcknowledgementId = acknowledgementId;
        ApplyConfirmedAt = confirmedAt;
        ConditionNormalizedAt = null;
        ConditionNormalizedValue = null;
        ReleaseAcknowledgementId = null;
        ReleaseConfirmedAt = null;
        LastError = null;
        EffectState = FdcInterlockEffectState.Applied;
        Version++;
    }

    internal void MarkConditionNormalized(DateTime at, decimal value)
    {
        if (IsResolved || EffectState < FdcInterlockEffectState.Applied)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot normalize from state '{EffectState}'.");
        if (string.IsNullOrWhiteSpace(ApplyAcknowledgementId) || ApplyConfirmedAt is null)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot normalize without confirmed apply evidence.");
        ConditionNormalizedAt = at;
        ConditionNormalizedValue = value;
        if (EffectState < FdcInterlockEffectState.ConditionNormalized)
            EffectState = FdcInterlockEffectState.ConditionNormalized;
        LastError = null;
        Version++;
    }
    internal void MarkReleasePending(string? error = null)
    {
        if (IsResolved || EffectState is not FdcInterlockEffectState.ConditionNormalized
            and not FdcInterlockEffectState.ReleasePending)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot await release from state '{EffectState}'.");
        EffectState = FdcInterlockEffectState.ReleasePending;
        LastError = error;
        Version++;
    }

    internal void MarkActionError(string error)
    {
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown action error." : error;
        Version++;
    }

    internal void MarkReleaseConfirmed(string? acknowledgementId, DateTime confirmedAt)
    {
        if (IsResolved || EffectState is not FdcInterlockEffectState.ConditionNormalized
            and not FdcInterlockEffectState.ReleasePending)
            throw new InvalidOperationException(
                $"Effect '{Id}' cannot confirm release from state '{EffectState}'.");
        if (string.IsNullOrWhiteSpace(acknowledgementId))
            throw new ArgumentException("Release acknowledgement ID is required.", nameof(acknowledgementId));
        ReleaseAcknowledgementId = acknowledgementId;
        ReleaseConfirmedAt = confirmedAt;
        LastError = null;
        Version++;
    }
}

public enum FdcInterlockEffectState
{
    Prepared,
    Applied,
    ConditionNormalized,
    ReleasePending,
    Resolved
}
