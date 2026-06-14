using NexaOne.Common;

namespace NexaOne.EST.Domain;

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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 ClearedAt/ElapsedSeconds를 복원하지 않아, 해제된 알람이 읽기경로에서 활성(IsActive=true)으로
    /// 오인되고 경과시간이 유실되는 상태손실을 막는다(GetActiveAlarms는 SQL 필터로 가려졌으나 손실 자체는 실재).</summary>
    public static EquipmentAlarm Restore(
        string alarmId, string equipmentId, string alarmCode, string alarmName, string alarmLevel,
        DateTime occurredAt, DateTime? clearedAt, long? elapsedSeconds)
        => new(alarmId)
        {
            EquipmentId = equipmentId,
            AlarmCode = alarmCode,
            AlarmName = alarmName,
            AlarmLevel = alarmLevel,
            OccurredAt = occurredAt,
            ClearedAt = clearedAt,
            ElapsedSeconds = elapsedSeconds
        };

    public void Clear(DateTime clearedAt)
    {
        ClearedAt = clearedAt;
        ElapsedSeconds = (long)(clearedAt - OccurredAt).TotalSeconds;
    }
}
