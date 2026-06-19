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
        // ADR-002: 알람 발생을 도메인 이벤트로 발행한다. 리포가 알람 기록과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        alarm.RaiseDomainEvent(new EquipmentAlarmRaisedDomainEvent(alarmId, equipmentId, alarmCode, alarmLevel));
        return alarm;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 ClearedAt/ElapsedSeconds를 복원하지 않아, 해제된 알람이 읽기경로에서 활성(IsActive=true)으로
    /// 오인되고 경과시간이 유실되는 상태손실을 막는다(GetActiveAlarms는 SQL 필터로 가려졌으나 손실 자체는 실재).</summary>
    public static EquipmentAlarm Restore(
        string alarmId, string equipmentId, string alarmCode, string alarmName, string alarmLevel,
        DateTime occurredAt, DateTime? clearedAt, long? elapsedSeconds,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var alarm = new EquipmentAlarm(alarmId)
        {
            EquipmentId = equipmentId,
            AlarmCode = alarmCode,
            AlarmName = alarmName,
            AlarmLevel = alarmLevel,
            OccurredAt = occurredAt,
            ClearedAt = clearedAt,
            ElapsedSeconds = elapsedSeconds
        };
        // 영속된 감사 메타데이터를 그대로 복원한다(미복원 시 매 읽기마다 CreatedAt이 UtcNow로 재생성되고
        // CreatedBy=""·UpdatedBy/At=null로 리셋되는 상태손실 방지). 인자 미지정 시 현재값 유지.
        alarm.RestoreAudit(createdBy ?? alarm.CreatedBy, createdAt ?? alarm.CreatedAt, updatedBy, updatedAt);
        return alarm;
    }

    public void Clear(DateTime clearedAt)
    {
        ClearedAt = clearedAt;
        var elapsed = (long)(clearedAt - OccurredAt).TotalSeconds;
        ElapsedSeconds = elapsed;
        // ADR-002: 알람 해제를 도메인 이벤트로 발행한다. 리포가 해제(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new EquipmentAlarmClearedDomainEvent(Id, EquipmentId, elapsed));
    }
}
