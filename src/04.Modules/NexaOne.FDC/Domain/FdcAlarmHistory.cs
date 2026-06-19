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
        // ADR-002: 알람 발생을 도메인 이벤트로 발행한다. 리포가 알람 이력과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        history.RaiseDomainEvent(new FdcAlarmRaisedDomainEvent(
            alarmId, alarmConfigId, equipmentId, parameterId, alarmLevel, triggerValue));
        return history;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create+Clear 재생 경로는 읽기 시마다 FdcAlarmRaised/FdcAlarmCleared를 유령 발행하므로,
    /// 이벤트 발행 없이 ClearedAt/IsCleared를 직접 복원해 읽기경로 부작용을 막는다.</summary>
    public static FdcAlarmHistory Restore(
        string alarmId, string alarmConfigId, string equipmentId, string parameterId, string alarmLevel,
        decimal triggerValue, string message, DateTime occurredAt, DateTime? clearedAt, bool isCleared,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var history = new FdcAlarmHistory(alarmId)
        {
            AlarmConfigId = alarmConfigId,
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            AlarmLevel = alarmLevel,
            TriggerValue = triggerValue,
            Message = message ?? string.Empty,
            OccurredAt = occurredAt,
            ClearedAt = clearedAt,
            IsCleared = isCleared
        };
        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 그대로 복원한다(미복원 시 CreatedAt이 매 읽기마다 재생성).
        history.RestoreAudit(createdBy ?? history.CreatedBy, createdAt ?? history.CreatedAt, updatedBy, updatedAt);
        return history;
    }

    /// <summary>알람 해제 — 해제 시각 기록, IS_CLEARED=true. (멱등)</summary>
    public void Clear(DateTime clearedAt)
    {
        if (IsCleared) return;
        ClearedAt = clearedAt;
        IsCleared = true;
        // ADR-002: 알람 해제를 도메인 이벤트로 발행한다. 리포가 해제(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new FdcAlarmClearedDomainEvent(Id, EquipmentId, clearedAt));
    }
}
