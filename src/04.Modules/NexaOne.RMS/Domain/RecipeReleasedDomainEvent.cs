using NexaOne.Common;

namespace NexaOne.RMS.Domain;

/// <summary>레시피 배포 도메인 이벤트(ADR-002). 배포(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 레시피별 순서 보장을 위해 RecipeId, Payload는 전이 후 상태·배포자·배포시각을 JSON으로 담는다.</summary>
public sealed record RecipeReleasedDomainEvent(
    string RecipeId, string State, string ReleaserId, DateTime ReleasedAt) : IOutboxEvent
{
    public string EventType => "RecipeReleased";
    public string Module => "RMS";
    public string AggregateId => RecipeId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { State, ReleaserId, ReleasedAt });
}
