using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>FDC 알람 발생/해제 이력 (FDC_ALARM_HISTORY, design 10.4.1).
/// 발생 시 1행 생성하고, 정상 복귀 시 <see cref="Clear"/>로 CLEARED_AT/IS_CLEARED를 갱신한다.</summary>
public sealed class FdcAlarmHistory : AuditableEntity<string>
{
    private FdcAlarmHistory(string alarmId) : base(alarmId) { }

    public string AlarmConfigId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string ParameterId { get; private set; } = string.Empty;
    public string AlarmLevel { get; private set; } = string.Empty;
    public decimal TriggerValue { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }
    public DateTime? ClearedAt { get; private set; }
    public bool IsCleared { get; private set; }

    public static Result<FdcAlarmHistory> Create(
        string alarmId,
        string alarmConfigId,
        string equipmentId,
        string parameterId,
        string alarmLevel,
        decimal triggerValue,
        string message,
        DateTime occurredAt)
    {
        if (string.IsNullOrWhiteSpace(alarmId))
            return Result.Failure<FdcAlarmHistory>(Error.Validation(nameof(alarmId), "Alarm ID is required."));
        if (string.IsNullOrWhiteSpace(alarmConfigId))
            return Result.Failure<FdcAlarmHistory>(Error.Validation(nameof(alarmConfigId), "Alarm config ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcAlarmHistory>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(parameterId))
            return Result.Failure<FdcAlarmHistory>(Error.Validation(nameof(parameterId), "Parameter ID is required."));

        var history = new FdcAlarmHistory(alarmId)
        {
            AlarmConfigId = alarmConfigId,
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            AlarmLevel = alarmLevel,
            TriggerValue = triggerValue,
            Message = message ?? string.Empty,
            OccurredAt = occurredAt,
            IsCleared = false
        };
        return history;
    }

    /// <summary>알람 해제 — 해제 시각 기록, IS_CLEARED=true. (멱등)</summary>
    public void Clear(DateTime clearedAt)
    {
        if (IsCleared) return;
        ClearedAt = clearedAt;
        IsCleared = true;
    }
}
