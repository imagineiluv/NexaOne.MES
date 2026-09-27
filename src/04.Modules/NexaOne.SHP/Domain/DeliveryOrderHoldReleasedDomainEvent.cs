using NexaOne.Common;

namespace NexaOne.SHP.Domain;

public sealed record DeliveryOrderHoldReleasedDomainEvent(string OrderId) : IOutboxEvent
{
    public string EventType => "DeliveryOrderHoldReleased";
    public string Module => "SHP";
    public string AggregateId => OrderId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { IsHeld = false });
}
