using NexaOne.Common;

namespace NexaOne.EST.Domain;

public sealed class EquipmentStateHistory : Entity<string>
{
    private EquipmentStateHistory(string historyId) : base(historyId) { }

    public string EquipmentId { get; private set; } = string.Empty;
    public string FromState { get; private set; } = string.Empty;
    public string ToState { get; private set; } = string.Empty;
    /// <summary>Actual applied state after matrix correction (may differ from ToState).</summary>
    public string SetState { get; private set; } = string.Empty;
    public DateTime ChangedAt { get; private set; }
    public string ChangedBy { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    /// <summary>UI / EQP / SYSTEM</summary>
    public string SourceType { get; private set; } = "UI";
    public string? TxnHistKey { get; private set; }
    public long? DurationSeconds { get; private set; }

    public static Result<EquipmentStateHistory> Create(
        string historyId,
        string equipmentId,
        string fromState,
        string toState,
        string setState,
        DateTime changedAt,
        string changedBy,
        string reason = "",
        string sourceType = "UI",
        string? txnHistKey = null)
    {
        if (string.IsNullOrWhiteSpace(historyId))
            return Result.Failure<EquipmentStateHistory>(Error.Validation(nameof(historyId), "History ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<EquipmentStateHistory>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));

        var history = new EquipmentStateHistory(historyId)
        {
            EquipmentId = equipmentId,
            FromState   = fromState,
            ToState     = toState,
            SetState    = string.IsNullOrEmpty(setState) ? toState : setState,
            ChangedAt   = changedAt,
            ChangedBy   = changedBy,
            Reason      = reason,
            SourceType  = sourceType,
            TxnHistKey  = txnHistKey
        };
        return history;
    }

    public void SetDuration(DateTime nextChangedAt)
        => DurationSeconds = (long)(nextChangedAt - ChangedAt).TotalSeconds;
}
