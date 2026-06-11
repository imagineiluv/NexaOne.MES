using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace NexaOne.Infrastructure.Messaging;

public sealed class KafkaMessageBus : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaMessageBus> _logger;
    private bool _disposed;

    public KafkaMessageBus(string bootstrapServers, ILogger<KafkaMessageBus> logger)
    {
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers  = bootstrapServers,
            Acks              = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs  = 10_000
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(
        string topic,
        DomainEventMessage message,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        try
        {
            var result = await _producer.ProduceAsync(topic,
                new Message<string, string> { Key = message.AggregateId, Value = json }, ct);
            _logger.LogDebug("Published {EventType} to {Topic} offset {Offset}",
                message.EventType, topic, result.Offset);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Kafka publish failed for {EventType} on {Topic}",
                message.EventType, topic);
            throw;
        }
    }

    public async Task PublishAsync(
        string topic,
        IEnumerable<DomainEventMessage> messages,
        CancellationToken ct = default)
    {
        foreach (var message in messages)
            await PublishAsync(topic, message, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        _disposed = true;
    }
}
