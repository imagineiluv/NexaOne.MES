using NexaOne.Common;

namespace NexaOne.EPT.Domain;

public sealed class EquipmentAlarm : AuditableEntity<string>
{
    private EquipmentAlarm(string alarmId) : base(alarmId) { }

    public string EquipmentId { get; private set; } = string.Empty;
    public string AlarmCode { get; private set; } = string.Empty;
    public string AlarmName { get; private set; } = string.Empty;
    public string AlarmLevel { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }
    public DateTime? ClearedAt { get; private set; }
    public long? ElapsedSeconds { get; private set; }
    public bool IsActive => ClearedAt is null;

    public static Result<EquipmentAlarm> Create(
        string alarmId,
        string equipmentId,
        string alarmCode,
        string alarmName,
        string alarmLevel,
        DateTime occurredAt)
    {
        if (string.IsNullOrWhiteSpace(alarmId))
            return Result.Failure<EquipmentAlarm>(Error.Validation(nameof(alarmId), "Alarm ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<EquipmentAlarm>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));

        var alarm = new EquipmentAlarm(alarmId)
        {
            EquipmentId = equipmentId,
            AlarmCode = alarmCode,
            AlarmName = alarmName,
            AlarmLevel = alarmLevel,
            OccurredAt = occurredAt
        };
        return alarm;
    }

    public void Clear(DateTime clearedAt)
    {
        ClearedAt = clearedAt;
        ElapsedSeconds = (long)(clearedAt - OccurredAt).TotalSeconds;
    }
}
