using NexaOne.Common;

namespace NexaOne.MDM.Domain;

/// <summary>설비 상위 변경 도메인 이벤트(ADR-002). 상위 변경(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 설비별 순서 보장을 위해 EquipmentId, Payload는 변경된 상위 설비 식별자를 JSON으로 담는다(해제 시 null).</summary>
public sealed record EquipmentParentChangedDomainEvent(string EquipmentId, string? ParentEquipmentId) : IOutboxEvent
{
    public string EventType => "EquipmentParentChanged";
    public string Module => "MDM";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { ParentEquipmentId });
}
