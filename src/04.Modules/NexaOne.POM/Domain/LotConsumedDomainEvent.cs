using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>Lot 소비 도메인 이벤트(ADR-002 — Mixing 투입 원자적 소비). Consume(UPDATE)과 동일 트랜잭션에
/// EES_OUTBOX로 기록된다(opt-in). AGGREGATE_ID는 Lot별 순서 보장을 위해 Id(LOT_ID), Payload는 소비된
/// 제품과 수량을 genealogy 추적용으로 JSON에 담는다(원자적 소비라 잔량 분할 없음).</summary>
public sealed record LotConsumedDomainEvent(string LotId, string ProductId, decimal Qty) : IOutboxEvent
{
    public string EventType => "LotConsumed";
    public string Module => "POM";
    public string AggregateId => LotId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { ProductId, Qty });
}
