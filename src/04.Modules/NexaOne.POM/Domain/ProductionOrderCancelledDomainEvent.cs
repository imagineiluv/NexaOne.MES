using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>생산오더 취소 도메인 이벤트(ADR-002). 취소(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 오더별 순서 보장을 위해 OrderId, Payload는 단일 값(ORDER_ID)이라 평문 문자열로 담는다.</summary>
public sealed record ProductionOrderCancelledDomainEvent(string OrderId) : IOutboxEvent
{
    public string EventType => "ProductionOrderCancelled";
    public string Module => "POM";
    public string AggregateId => OrderId;
    public string Payload => OrderId;
}
