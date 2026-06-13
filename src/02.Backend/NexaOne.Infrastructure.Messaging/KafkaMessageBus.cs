using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using NexusCom.Messaging.Kafka;

namespace NexaOne.Infrastructure.Messaging;

/// <summary>도메인 이벤트 발행 글루 — Kafka 프로토콜 본체는 NexusCom.Messaging.Kafka의
/// KafkaDriver가 소유하고(§3.6.1), 본 클래스는 직렬화·토픽 정책만 담당한다.</summary>
public sealed class KafkaMessageBus : IDisposable
{
    private readonly KafkaDriver _driver;
    private readonly ILogger<KafkaMessageBus> _logger;

    public KafkaMessageBus(
        string bootstrapServers,
        ILogger<KafkaMessageBus> logger,
        ILogger<KafkaDriver> driverLogger)
    {
        _logger = logger;
        _driver = new KafkaDriver(driverLogger);
        _driver.Configure(bootstrapServers, messageTimeoutMs: 10_000);
    }

    public async Task PublishAsync(
        string topic,
        DomainEventMessage message,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        try
        {
            await _driver.ProduceAsync(topic, message.AggregateId, json, ct);
            _logger.LogDebug("Published {EventType} to {Topic}", message.EventType, topic);
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

    public void Dispose() => _driver.Dispose();
}
