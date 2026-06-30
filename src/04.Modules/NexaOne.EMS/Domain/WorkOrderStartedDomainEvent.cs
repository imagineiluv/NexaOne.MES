using NexaOne.Common;

namespace NexaOne.EMS.Domain;

/// <summary>작업지시 착수 도메인 이벤트(ADR-002). 착수(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 작업지시별 순서 보장을 위해 WO_ID(Id),
/// Payload는 표시·후속처리에 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record WorkOrderStartedDomainEvent(
    string WoId, string EquipmentId, string AssigneeId, DateTime StartedAt) : IOutboxEvent
{
    public string EventType => "WorkOrderStarted";
    public string Module => "EMS";
    public string AggregateId => WoId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId, AssigneeId, StartedAt });
}
