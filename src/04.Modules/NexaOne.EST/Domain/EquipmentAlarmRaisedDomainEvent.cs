using NexaOne.Common;

namespace NexaOne.EST.Domain;

/// <summary>설비 알람 발생 도메인 이벤트(ADR-002 — 상태 슬라이스를 알람 애그리거트로 확장). 알람 기록과 동일
/// 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent) 디스패처가 실시간 구독자(SignalR)에게 발행한다.
/// AGGREGATE_ID는 설비별 순서 보장을 위해 EquipmentId, Payload는 알람 식별·표시에 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record EquipmentAlarmRaisedDomainEvent(
    string AlarmId, string EquipmentId, string AlarmCode, string AlarmLevel) : IOutboxEvent
{
    public string EventType => "EquipmentAlarmRaised";
    public string Module => "EST";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { AlarmId, AlarmCode, AlarmLevel });
}
