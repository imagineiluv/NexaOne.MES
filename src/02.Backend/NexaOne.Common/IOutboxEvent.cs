namespace NexaOne.Common;

/// <summary>
/// Outbox로 발행 가능한 도메인 이벤트(ADR-002). 리포지토리가 데이터 변경과 동일 트랜잭션에 EES_OUTBOX 행으로
/// 기록할 수 있도록 발행에 필요한 봉투 필드를 노출한다. 도메인 이벤트가 이 계약을 구현하면 리포가 종류를
/// 몰라도 일괄 매핑할 수 있다(트랜잭션 outbox의 도메인↔인프라 경계).
/// </summary>
public interface IOutboxEvent : IDomainEvent
{
    string EventType { get; }
    string Module { get; }
    string AggregateId { get; }
    string Payload { get; }
}
