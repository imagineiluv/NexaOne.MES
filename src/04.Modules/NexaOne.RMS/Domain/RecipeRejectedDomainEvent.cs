using NexaOne.Common;

namespace NexaOne.RMS.Domain;

/// <summary>레시피 반려 도메인 이벤트(ADR-002). 반려(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 레시피별 순서 보장을 위해 RecipeId, Payload는 전이 후 상태와 반려 사유를 JSON으로 담는다.</summary>
public sealed record RecipeRejectedDomainEvent(
    string RecipeId, string State, string Reason) : IOutboxEvent
{
    public string EventType => "RecipeRejected";
    public string Module => "RMS";
    public string AggregateId => RecipeId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { State, Reason });
}
