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

    /// <summary>
    /// 다건 발행 — <b>원자적이지 않다.</b> 메시지를 순차로 발행하므로 중간(k번째)에서 실패하면
    /// 0..k-1은 이미 브로커에 커밋된 채 예외가 전파된다(부분 발행). 전체 원자성이 필요하면
    /// Kafka 트랜잭션이 필요하며 현재 드라이버는 이를 노출하지 않는다. 소비 측 멱등 처리를 전제로 사용한다.
    /// </summary>
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
