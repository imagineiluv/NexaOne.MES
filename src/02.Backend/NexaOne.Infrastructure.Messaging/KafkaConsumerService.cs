using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NexaOne.Infrastructure.Messaging;

/// <summary>도메인 이벤트 구독 글루 — Consumer 생성(프로토콜 설정)은 NexusCom KafkaDriver에
/// 위임하고(§3.6.1), 본 클래스는 역직렬화·핸들러 호출·커밋 정책만 담당한다.</summary>
public sealed class KafkaConsumerService : BackgroundService
{
    private readonly Func<Action<string>?, IConsumer<string, string>> _consumerFactory;
    private readonly KafkaConsumerOptions _options;
    private readonly Func<DomainEventMessage, CancellationToken, Task> _handler;
    private readonly ILogger<KafkaConsumerService> _logger;

    public KafkaConsumerService(
        KafkaMessageBus messageBus,
        KafkaConsumerOptions options,
        Func<DomainEventMessage, CancellationToken, Task> handler,
        ILogger<KafkaConsumerService> logger)
        : this(onError => messageBus.CreateConsumer(options, onError), options, handler, logger)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
    }

    internal KafkaConsumerService(
        Func<Action<string>?, IConsumer<string, string>> consumerFactory,
        KafkaConsumerOptions options,
        Func<DomainEventMessage, CancellationToken, Task> handler,
        ILogger<KafkaConsumerService> logger)
    {
        _consumerFactory = consumerFactory ?? throw new ArgumentNullException(nameof(consumerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.GroupId);
        if (_options.Topics.Count == 0 || _options.Topics.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty Kafka topic is required.", nameof(options));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Confluent.Kafka consumes synchronously. Yield before entering that loop so
        // BackgroundService.StartAsync never blocks host startup waiting for a broker poll.
        await Task.Yield();

        using var consumer = _consumerFactory(
            reason => _logger.LogError("Kafka consumer error: {Reason}", reason));

        consumer.Subscribe(_options.Topics);
        _logger.LogInformation("Kafka consumer started for topics: {Topics}", string.Join(", ", _options.Topics));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result = null;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex) when (ex.Error.IsFatal)
                {
                    _logger.LogError(ex, "Fatal Kafka consume error");
                    break;
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning(ex, "Kafka consume error, continuing");
                    continue;
                }

                if (result is null) continue;

                DomainEventMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<DomainEventMessage>(result.Message.Value);
                }
                catch (JsonException ex)
                {
                    // 역직렬화 불가(포이즌) 메시지는 커밋해 건너뛴다 — 재시작마다 무한 재처리 방지
                    _logger.LogError(ex, "Malformed Kafka message from {Topic}, skipping", result.Topic);
                    consumer.Commit(result);
                    continue;
                }
                if (message is null) { consumer.Commit(result); continue; }

                // 핸들러가 max.poll.interval.ms(5분)를 넘기면 그룹에서 추방되어 커밋 실패·중복 처리가 발생한다.
                // 처리 동안 파티션을 Pause해 추가 페치를 막고, 짧은 Consume로 폴을 유지해 세션을 살린다(추방 방지).
                // 핸들러는 at-least-once 전제로 멱등해야 한다.
                var partitions = consumer.Assignment;
                consumer.Pause(partitions);
                try
                {
                    var handlerTask = _handler(message, stoppingToken);
                    while (!handlerTask.IsCompleted)
                        consumer.Consume(TimeSpan.FromMilliseconds(200));   // Pause 상태 → null, 폴 진행만 통지
                    await handlerTask;

                    consumer.Commit(result);
                    _logger.LogDebug("Consumed and committed {EventType} from {Topic}",
                        message.EventType, result.Topic);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;   // graceful shutdown — 에러 아님
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling Kafka message from {Topic}", result.Topic);
                }
                finally
                {
                    consumer.Resume(partitions);
                }
            }
        }
        finally
        {
            consumer.Close();
            _logger.LogInformation("Kafka consumer stopped");
        }
    }
}

public sealed class KafkaConsumerOptions
{
    public string GroupId { get; set; } = "nexaone-mes";
    public IReadOnlyList<string> Topics { get; set; } = new[] { "nexaone.events" };
}
