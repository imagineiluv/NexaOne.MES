using NexaOne.Common;

namespace NexaOne.MDM.Domain;

/// <summary>설비 비활성화 도메인 이벤트(ADR-002). 비활성화(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 설비별 순서 보장을 위해 EquipmentId, Payload는 전이 후 상태를 JSON으로 담는다.</summary>
public sealed record EquipmentDeactivatedDomainEvent(string EquipmentId) : IOutboxEvent
{
    public string EventType => "EquipmentDeactivated";
    public string Module => "MDM";
    public string AggregateId => EquipmentId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { NewValidState = "Invalid" });
}
