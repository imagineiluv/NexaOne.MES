using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>생산오더 착수 도메인 이벤트(ADR-002). 착수(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 오더별 순서 보장을 위해 OrderId(=ORDER_ID),
/// Payload는 실제 착수에 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record ProductionOrderStartedDomainEvent(
    string OrderId, string EquipmentId, string ProductId, DateTime ActualStart) : IOutboxEvent
{
    public string EventType => "ProductionOrderStarted";
    public string Module => "POM";
    public string AggregateId => OrderId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId, ProductId, ActualStart });
}
