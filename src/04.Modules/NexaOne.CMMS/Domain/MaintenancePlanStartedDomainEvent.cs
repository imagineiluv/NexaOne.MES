using NexaOne.Common;

namespace NexaOne.CMMS.Domain;

/// <summary>정비계획 착수 도메인 이벤트(ADR-002). 착수(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 구독자에게 발행한다. AGGREGATE_ID는 PLAN_ID(정비계획별 순서 보장), Payload는 후속 처리에 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record MaintenancePlanStartedDomainEvent(
    string PlanId, string EquipmentId, string AssigneeId, DateTime ScheduledDate) : IOutboxEvent
{
    public string EventType => "MaintenancePlanStarted";
    public string Module => "CMMS";
    public string AggregateId => PlanId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId, AssigneeId, ScheduledDate });
}
