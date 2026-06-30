using NexaOne.Common;

namespace NexaOne.EMS.Domain;

/// <summary>작업지시 완료 도메인 이벤트(ADR-002). 완료(UPDATE)와 동일 트랜잭션에 EES_OUTBOX로 기록된다.
/// AGGREGATE_ID는 착수 이벤트와 동일하게 WO_ID(Id, 작업지시별 순서 보장), Payload는 완료시각·고장코드·비고를 JSON으로 담는다.</summary>
public sealed record WorkOrderCompletedDomainEvent(
    string WoId, string EquipmentId, DateTime CompletedAt, string? FailureCodeId, string? Remark) : IOutboxEvent
{
    public string EventType => "WorkOrderCompleted";
    public string Module => "EMS";
    public string AggregateId => WoId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId, CompletedAt, FailureCodeId, Remark });
}
