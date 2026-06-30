using NexaOne.Common;

namespace NexaOne.EMS.Domain;

/// <summary>작업지시 취소 도메인 이벤트(ADR-002). 취소(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 WO_ID(Id, 작업지시별 순서 보장), Payload는 설비 식별을 위한 단일 EquipmentId를 JSON으로 담는다.</summary>
public sealed record WorkOrderCancelledDomainEvent(
    string WoId, string EquipmentId) : IOutboxEvent
{
    public string EventType => "WorkOrderCancelled";
    public string Module => "EMS";
    public string AggregateId => WoId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId });
}
