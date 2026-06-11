using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace NexaOne.Driver.Kafka;

public sealed class KafkaDriver : IDisposable
{
    private readonly ILogger<KafkaDriver> _logger;
    private IProducer<string, string>? _producer;
    private bool _disposed;

    public KafkaDriver(ILogger<KafkaDriver> logger)
    {
        _logger = logger;
    }

    public void Configure(string bootstrapServers)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task ProduceAsync(
        string topic,
        string key,
        string value,
        CancellationToken ct = default)
    {
        if (_producer is null) throw new InvalidOperationException("KafkaDriver not configured.");

        try
        {
            var result = await _producer.ProduceAsync(topic,
                new Message<string, string> { Key = key, Value = value }, ct);
            _logger.LogDebug("Kafka produced to {Topic} partition {Partition} offset {Offset}",
                topic, result.Partition, result.Offset);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Kafka produce error on topic {Topic}", topic);
            throw;
        }
    }

    public IConsumer<string, string> CreateConsumer(string groupId, string bootstrapServers)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        return new ConsumerBuilder<string, string>(config).Build();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
        _disposed = true;
    }
}
