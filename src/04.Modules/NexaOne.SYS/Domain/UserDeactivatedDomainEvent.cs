using NexaOne.Common;

namespace NexaOne.SYS.Domain;

/// <summary>사용자 비활성화 도메인 이벤트(ADR-002). 비활성화(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 사용자별 순서 보장을 위해 UserId, Payload는 식별에 필요한 UserId를 JSON으로 담는다.</summary>
public sealed record UserDeactivatedDomainEvent(string UserId) : IOutboxEvent
{
    public string EventType => "UserDeactivated";
    public string Module => "SYS";
    public string AggregateId => UserId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { UserId });
}
