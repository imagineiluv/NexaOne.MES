using NexaOne.Common;

namespace NexaOne.SYS.Domain;

/// <summary>사용자 신청 승인 도메인 이벤트(ADR-002). 승인(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 신청별 순서 보장을 위해 RequestId, Payload는 승인 대상·승인자·승인시각을 JSON으로 담는다.</summary>
public sealed record UserRequestApprovedDomainEvent(
    string RequestId, string UserId, string ApprovedBy, DateTime ApprovedAt) : IOutboxEvent
{
    public string EventType => "UserRequestApproved";
    public string Module => "SYS";
    public string AggregateId => RequestId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { UserId, ApprovedBy, ApprovedAt });
}
