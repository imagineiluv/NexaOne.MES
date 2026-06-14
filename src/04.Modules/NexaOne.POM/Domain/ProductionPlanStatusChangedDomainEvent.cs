using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>생산계획 상태 변경 도메인 이벤트(ADR-002). 상태 전이(Release/Start/Complete/Cancel)의 UPDATE와 동일
/// 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent) 디스패처가 실시간 구독자에게 발행한다.
/// AGGREGATE_ID는 계획별 순서 보장을 위해 PlanId(PLAN_ID), Payload는 전이된 새 상태를 JSON으로 담는다.</summary>
public sealed record ProductionPlanStatusChangedDomainEvent(
    string PlanId, ProductionPlanStatus NewStatus) : IOutboxEvent
{
    public string EventType => "ProductionPlanStatusChanged";
    public string Module => "POM";
    public string AggregateId => PlanId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { NewStatus = NewStatus.ToString() });
}
