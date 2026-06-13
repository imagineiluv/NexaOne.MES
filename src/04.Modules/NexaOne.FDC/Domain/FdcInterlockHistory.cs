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
        return history;
    }

    /// <summary>인터락 해제 — 해제 시각을 기록하고 IS_RESOLVED를 true로 한다. (멱등)</summary>
    public void Resolve(DateTime resolvedAt)
    {
        if (IsResolved) return;
        ResolvedAt = resolvedAt;
        IsResolved = true;
    }
}
