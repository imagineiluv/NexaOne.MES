using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>인터락 해제 도메인 이벤트(ADR-002). 해제(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 발동 이벤트와 동일하게 EquipmentId(설비별 순서 보장), Payload는 수집기의 즉시
/// 알림과 동일한 canonical 필드 및 해제 시각을 담는다.</summary>
public sealed record FdcInterlockResolvedDomainEvent(
    string EffectId,
    string RuleId,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    DateTime ResolvedAt) : IOutboxEvent
{
    public string EventType => "InterlockResolved";
    public string Module => "FDC";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(
        new { EffectId, RuleId, ParameterId, Value, ResolvedAt });
}
