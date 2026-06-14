using NexaOne.Common;

namespace NexaOne.RMS.Domain;

/// <summary>레시피 승인요청 도메인 이벤트(ADR-002). 승인요청(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 레시피별 순서 보장을 위해 RecipeId, Payload는 전이 후 상태를 담는다.</summary>
public sealed record RecipeApprovalRequestedDomainEvent(string RecipeId, string State) : IOutboxEvent
{
    public string EventType => "RecipeApprovalRequested";
    public string Module => "RMS";
    public string AggregateId => RecipeId;
    public string Payload => State;
}
