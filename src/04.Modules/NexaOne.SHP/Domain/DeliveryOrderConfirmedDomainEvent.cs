using NexaOne.Common;

namespace NexaOne.SHP.Domain;

/// <summary>출하지시 확정 도메인 이벤트(ADR-002). 확정(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 주문별 순서 보장을 위해 OrderId, Payload는 전이 후 상태를 JSON으로 담는다.</summary>
public sealed record DeliveryOrderConfirmedDomainEvent(string OrderId) : IOutboxEvent
{
    public string EventType => "DeliveryOrderConfirmed";
    public string Module => "SHP";
    public string AggregateId => OrderId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { NewStatus = "Confirmed" });
}
