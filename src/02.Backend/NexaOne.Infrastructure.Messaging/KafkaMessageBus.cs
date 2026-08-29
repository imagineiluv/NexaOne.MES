using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexaDB.Messaging.Kafka;

namespace NexaOne.Infrastructure.Messaging;

/// <summary>도메인 이벤트 발행 글루 — Kafka 프로토콜 본체는 NexaDB.Messaging.Kafka의
/// KafkaDriver가 소유하고(§3.6.1), 본 클래스는 직렬화·토픽 정책만 담당한다. <see cref="IMessageBus"/>
/// 구현체로서 OutboxDispatcher의 발행 백본이 된다(ADR-002).</summary>
public sealed class KafkaMessageBus : IMessageBus, IDisposable
{
    private readonly KafkaDriver _driver;
    private readonly ILogger<KafkaMessageBus> _logger;
    private readonly string _bootstrapServers;

    public KafkaMessageBus(
        string bootstrapServers,
        ILogger<KafkaMessageBus> logger,
        ILogger<KafkaDriver> driverLogger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);
        _logger = logger;
        _bootstrapServers = bootstrapServers;
        _driver = new KafkaDriver(driverLogger);
        _driver.Configure(bootstrapServers, messageTimeoutMs: 10_000);
    }

    /// <summary>server.xml 등 DI 컨테이너용 — bootstrapServers만 받고 로거는 NullLogger로 떨군다(드라이버는
    /// 메시징 빈으로 server.xml에 두고 GetBean으로 당겨 쓰는 패턴, ADR-006). 진단 로그가 필요하면 전체 ctor를 쓴다.</summary>
    public KafkaMessageBus(string bootstrapServers)
        : this(bootstrapServers, NullLogger<KafkaMessageBus>.Instance, NullLogger<KafkaDriver>.Instance)
    {
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

    internal IConsumer<string, string> CreateConsumer(
        KafkaConsumerOptions options,
        Action<string>? onError)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _driver.CreateConsumer(
            options.GroupId,
            _bootstrapServers,
            sessionTimeoutMs: 10_000,
            maxPollIntervalMs: 300_000,
            onError: onError);
    }

    /// <summary>
    /// Probes real broker metadata without publishing a synthetic message. Bootstrap addresses and
    /// broker errors stay inside this implementation and must not be copied into public diagnostics.
    /// </summary>
    internal async ValueTask ProbeBrokerAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Probe timeout must be positive.");
        cancellationToken.ThrowIfCancellationRequested();

        await RunBlockingProbeAsync(() =>
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = _bootstrapServers,
            }).Build();
            var metadata = admin.GetMetadata(timeout);
            if (metadata.Brokers.Count == 0)
                throw new KafkaException(new Error(ErrorCode.Local_AllBrokersDown));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the synchronous librdkafka metadata call off the caller thread and lets the caller
    /// observe cancellation immediately. The native probe itself remains bounded by its explicit
    /// metadata timeout and owns its resources until that bounded operation finishes.
    /// </summary>
    internal static Task RunBlockingProbeAsync(
        Action probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        cancellationToken.ThrowIfCancellationRequested();

        var probeTask = Task.Run(probe, CancellationToken.None);
        return probeTask.WaitAsync(cancellationToken);
    }

    public void Dispose() => _driver.Dispose();
}
