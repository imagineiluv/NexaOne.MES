using NexaOne.Common;

namespace NexaOne.SHP.Domain;

public sealed record DeliveryOrderHeldDomainEvent(string OrderId) : IOutboxEvent
{
    public string EventType => "DeliveryOrderHeld";
    public string Module => "SHP";
    public string AggregateId => OrderId;
    public string Payload => System.Text.Json.JsonSerializer.Serialize(new { IsHeld = true });
}
