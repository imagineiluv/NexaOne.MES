namespace NexaOne.Infrastructure.Messaging;

public sealed class DomainEventMessage
{
    public string EventType { get; init; } = string.Empty;
    public string AggregateId { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    public static DomainEventMessage Create(string eventType, string module, string aggregateId, string payload) =>
        new() { EventType = eventType, Module = module, AggregateId = aggregateId, Payload = payload };
}
