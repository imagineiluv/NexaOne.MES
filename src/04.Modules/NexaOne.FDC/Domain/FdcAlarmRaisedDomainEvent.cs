using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>FDC 알람 발생 도메인 이벤트(ADR-002 — EST 알람 슬라이스를 FDC로 미러링). 알람 이력 행과 동일
/// 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent) 디스패처가 실시간 구독자(SignalR)에게 발행한다.
/// AGGREGATE_ID는 설비별 순서 보장을 위해 EquipmentId, Payload는 알람 식별·표시에 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record FdcAlarmRaisedDomainEvent(
    string AlarmId, string AlarmConfigId, string EquipmentId, string ParameterId, string AlarmLevel, decimal TriggerValue) : IOutboxEvent
{
    public string EventType => "FdcAlarmRaised";
    public string Module => "FDC";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { AlarmId, AlarmConfigId, ParameterId, AlarmLevel, TriggerValue });
}
