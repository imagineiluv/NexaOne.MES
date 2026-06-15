using NexaOne.Common;

namespace NexaOne.SYS.Domain;

/// <summary>계정 잠금 도메인 이벤트(ADR-002). 잠금 전이는 동시 실패 시 증가 유실을 막는 '원자 SQL UPDATE'
/// (UserRepository.RecordLoginFailureAsync)가 소유하므로, 다른 애그리거트처럼 DomainEvents로 발행하지 않고
/// 리포가 동일 트랜잭션에 EES_OUTBOX로 '조건부' 기록한다(이번 실패로 임계 도달해 새로 잠긴 경우 1건).
/// 이 레코드는 그 봉투(EventType/Module/AggregateId/Payload)를 정의·직렬화하는 용도다. 다운스트림: 세션 무효화·보안감사.</summary>
public sealed record UserAccountLockedDomainEvent(string UserId, DateTime LockedUntil, int FailCount) : IOutboxEvent
{
    public string EventType => "UserAccountLocked";
    public string Module => "SYS";
    public string AggregateId => UserId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { LockedUntil, FailCount });
}
