using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>인터락 해제 도메인 이벤트(ADR-002). 해제(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 발동 이벤트와 동일하게 EquipmentId(설비별 순서 보장), Payload는 이력 식별과 해제 시각을 JSON으로 담는다.</summary>
public sealed record FdcInterlockResolvedDomainEvent(
    string HistoryId, string RuleId, string EquipmentId, DateTime ResolvedAt) : IOutboxEvent
{
    public string EventType => "FdcInterlockResolved";
    public string Module => "FDC";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { HistoryId, RuleId, ResolvedAt });
}
