using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>Lot TrackOut 도메인 이벤트(ADR-002). TrackOut(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록된다(opt-in).
/// AGGREGATE_ID는 Lot별 순서 보장을 위해 Id(LOT_ID), Payload는 전이 후 수량·상태·공정 위치를 JSON으로 담는다.</summary>
public sealed record LotTrackedOutDomainEvent(
    string LotId, decimal Qty, decimal DefectQty, LotState NewState, int CurrentStepIndex, bool IsLastStep) : IOutboxEvent
{
    public string EventType => "LotTrackedOut";
    public string Module => "POM";
    public string AggregateId => LotId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { Qty, DefectQty, NewState = NewState.ToString(), CurrentStepIndex, IsLastStep });
}
