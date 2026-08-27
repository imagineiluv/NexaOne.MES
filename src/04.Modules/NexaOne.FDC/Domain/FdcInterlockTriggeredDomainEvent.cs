using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>인터락 규칙 발동 도메인 이벤트(ADR-002). 발동 이력(INSERT)과 동일 트랜잭션에 EES_OUTBOX로 기록되어
/// (IOutboxEvent) 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 설비별 순서 보장을 위해 EquipmentId,
/// Payload는 수집기의 즉시 알림과 동일한 canonical 필드를 담는다. EffectId는 같은 위반 episode의
/// direct bus/outbox 재전달을 소비자가 멱등 처리할 수 있는 안정 식별자이며 이력 PK와 같다.</summary>
public sealed record FdcInterlockTriggeredDomainEvent(
    string EffectId,
    string RuleId,
    string EquipmentId,
    string ParameterId,
    string Action,
    string Message,
    decimal Value) : IOutboxEvent
{
    public string EventType => "InterlockTriggered";
    public string Module => "FDC";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(
        new { EffectId, RuleId, ParameterId, Action, Message, Value });
}
