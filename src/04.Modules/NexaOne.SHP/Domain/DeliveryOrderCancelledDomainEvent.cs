using NexaOne.Common;

namespace NexaOne.SHP.Domain;

/// <summary>출하지시 취소 도메인 이벤트(ADR-002). 취소(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 주문별 순서 보장을 위해 OrderId, Payload는 전이 후 상태를 JSON으로 담는다.</summary>
public sealed record DeliveryOrderCancelledDomainEvent(string OrderId) : IOutboxEvent
{
    public string EventType => "DeliveryOrderCancelled";
    public string Module => "SHP";
    public string AggregateId => OrderId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { NewStatus = "Cancelled" });
}
