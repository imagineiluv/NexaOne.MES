using NexaOne.Common;

namespace NexaOne.QMS.Domain;

/// <summary>부적합 확정 도메인 이벤트(ADR-002). 확정(UPDATE)과 동일 트랜잭션에 EES_OUTBOX로 기록되어(IOutboxEvent)
/// 디스패처가 실시간 구독자에게 발행한다. AGGREGATE_ID는 Lot별 순서 보장을 위해 LotId, Payload는 부적합 식별·표시에
/// 필요한 다중 필드를 JSON으로 담는다.</summary>
public sealed record DefectConfirmedDomainEvent(
    string DefectId, string LotId, string DefectClassId, string EquipmentId,
    int DefectCount, decimal DefectRate, string ConfirmedBy, DateTime ConfirmedAt) : IOutboxEvent
{
    public string EventType => "DefectConfirmed";
    public string Module => "QMS";
    public string AggregateId => LotId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new
    {
        DefectId, DefectClassId, EquipmentId, DefectCount, DefectRate, ConfirmedBy, ConfirmedAt
    });
}
