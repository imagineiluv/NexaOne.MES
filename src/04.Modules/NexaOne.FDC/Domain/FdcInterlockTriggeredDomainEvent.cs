using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>인터락 규칙 발동 도메인 이벤트(ADR-002). 발동 이력(INSERT)과 동일 트랜잭션에 EES_OUTBOX로 기록되어
/// (IOutboxEvent) 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 설비별 순서 보장을 위해 EquipmentId,
/// Payload는 이력 식별·규칙·동작에 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record FdcInterlockTriggeredDomainEvent(
    string HistoryId, string RuleId, string EquipmentId, string ParameterId, string Action, decimal TriggerValue) : IOutboxEvent
{
    public string EventType => "FdcInterlockTriggered";
    public string Module => "FDC";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { HistoryId, RuleId, ParameterId, Action, TriggerValue });
}
