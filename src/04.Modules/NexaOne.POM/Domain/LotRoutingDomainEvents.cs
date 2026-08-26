using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>LOT 라우팅 통제 수준 변경을 외부 구독자에게 알리는 outbox 이벤트다.</summary>
public sealed record LotRoutingControlModeChangedDomainEvent(
    string LotId,
    RoutingControlMode PreviousMode,
    RoutingControlMode NewMode,
    string Reason,
    string ChangedBy) : IOutboxEvent
{
    public string EventType => "LotRoutingControlModeChanged";
    public string Module => "POM";
    public string AggregateId => LotId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new
    {
        PreviousMode = PreviousMode.ToString(),
        NewMode = NewMode.ToString(),
        Reason,
        ChangedBy
    });
}

/// <summary>Bypass·대체·순서변경·재작업·복귀가 실제 LOT에 적용된 사실을 발행한다.</summary>
public sealed record LotRouteDeviationAppliedDomainEvent(
    string LotId,
    RouteDeviationType DeviationType,
    int FromStepIndex,
    int ToStepIndex,
    RoutingControlMode ControlMode,
    string Reason,
    string? ExceptionId) : IOutboxEvent
{
    public string EventType => "LotRouteDeviationApplied";
    public string Module => "POM";
    public string AggregateId => LotId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new
    {
        DeviationType = DeviationType.ToString(),
        FromStepIndex,
        ToStepIndex,
        ControlMode = ControlMode.ToString(),
        Reason,
        ExceptionId
    });
}
