using NexaOne.Common;

namespace NexaOne.SYS.Domain;

/// <summary>사용자 신청 반려 도메인 이벤트(ADR-002). 반려(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 신청별 순서 보장을 위해 RequestId, Payload는 반려 대상·반려자·반려사유를 JSON으로 담는다.</summary>
public sealed record UserRequestRejectedDomainEvent(
    string RequestId, string UserId, string RejectedBy, string RejectReason) : IOutboxEvent
{
    public string EventType => "UserRequestRejected";
    public string Module => "SYS";
    public string AggregateId => RequestId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { UserId, RejectedBy, RejectReason });
}
