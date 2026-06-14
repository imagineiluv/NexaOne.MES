using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>Lot TrackIn 도메인 이벤트(ADR-002). TrackIn(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록된다(opt-in).
/// AGGREGATE_ID는 Lot별 순서 보장을 위해 Id(LOT_ID), Payload는 점유 설비·Recipe·진입 공정을 JSON으로 담는다.</summary>
public sealed record LotTrackedInDomainEvent(
    string LotId, string EquipmentId, string? RecipeDefId, int? RecipeDefVersion, string ProcessId) : IOutboxEvent
{
    public string EventType => "LotTrackedIn";
    public string Module => "POM";
    public string AggregateId => LotId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { EquipmentId, RecipeDefId, RecipeDefVersion, ProcessId });
}
