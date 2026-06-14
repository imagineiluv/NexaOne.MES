using NexaOne.Common;

namespace NexaOne.EST.Domain;

/// <summary>설비 상태 전이 도메인 이벤트(ADR-002 대표 슬라이스). 상태 변경과 동일 트랜잭션에 EES_OUTBOX로
/// 기록되어(IOutboxEvent), 디스패처가 실시간 구독자(SignalR)에게 발행한다. Payload는 전이된 새 상태.</summary>
public sealed record EquipmentStateChangedDomainEvent(string EquipmentId, string NewState) : IOutboxEvent
{
    public string EventType => "EquipmentStateChanged";
    public string Module => "EST";
    public string AggregateId => EquipmentId;
    public string Payload => NewState;
}
