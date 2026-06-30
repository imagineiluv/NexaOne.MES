using NexaOne.Common;

namespace NexaOne.EMS.Domain;

/// <summary>정비계획 완료 도메인 이벤트(ADR-002). 완료(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 PLAN_ID(정비계획별 순서 보장), Payload는 대상 설비 식별을 JSON으로 담는다.</summary>
public sealed record MaintenancePlanCompletedDomainEvent(
    string PlanId, string EquipmentId) : IOutboxEvent
{
    public string EventType => "MaintenancePlanCompleted";
    public string Module => "EMS";
    public string AggregateId => PlanId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId });
}
