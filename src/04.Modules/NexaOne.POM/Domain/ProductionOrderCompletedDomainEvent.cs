using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>생산오더 완료 도메인 이벤트(ADR-002). 완료(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 착수 이벤트와 동일하게 OrderId(오더별 순서 보장), Payload는 실적 수량·완료시각을 JSON으로 담는다.</summary>
public sealed record ProductionOrderCompletedDomainEvent(
    string OrderId, decimal ActualQty, DateTime ActualEnd) : IOutboxEvent
{
    public string EventType => "ProductionOrderCompleted";
    public string Module => "POM";
    public string AggregateId => OrderId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { ActualQty, ActualEnd });
}
