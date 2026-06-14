using NexaOne.Common;

namespace NexaOne.EST.Domain;

/// <summary>설비 알람 해제 도메인 이벤트(ADR-002). 해제(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 발생 이벤트와 동일하게 EquipmentId(설비별 순서 보장), Payload는 알람 식별과 경과시간(초)을 JSON으로 담는다.</summary>
public sealed record EquipmentAlarmClearedDomainEvent(
    string AlarmId, string EquipmentId, long ElapsedSeconds) : IOutboxEvent
{
    public string EventType => "EquipmentAlarmCleared";
    public string Module => "EST";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { AlarmId, ElapsedSeconds });
}
