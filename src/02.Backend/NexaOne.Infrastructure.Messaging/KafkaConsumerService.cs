using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexa.Components.Messaging.Kafka;

namespace NexaOne.Infrastructure.Messaging;

/// <summary>Owns event deserialization, handler lifetime and manual commit policy over a Component session.</summary>
public sealed class KafkaConsumerService : BackgroundService
{
    private readonly Func<KafkaConsumerOptions, Action<string>?, IKafkaConsumerSession> _consumerFactory;
    private readonly KafkaConsumerOptions _options;
    private readonly Func<DomainEventMessage, CancellationToken, Task> _handler;
    private readonly ILogger<KafkaConsumerService> _logger;

    public KafkaConsumerService(
        KafkaMessageBus messageBus,
        KafkaConsumerOptions options,
        Func<DomainEventMessage, CancellationToken, Task> handler,
        ILogger<KafkaConsumerService> logger)
        : this((settings, onError) => messageBus.CreateConsumer(settings, onError), options, handler, logger)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
    }

    internal KafkaConsumerService(
        Func<KafkaConsumerOptions, Action<string>?, IKafkaConsumerSession> consumerFactory,
        KafkaConsumerOptions options,
        Func<DomainEventMessage, CancellationToken, Task> handler,
        ILogger<KafkaConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _consumerFactory = consumerFactory ?? throw new ArgumentNullException(nameof(consumerFactory));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GroupId);
        var topics = options.Topics?.ToArray();
        if (topics is null || topics.Length == 0 || topics.Any(string.IsNullOrWhiteSpace)
            || topics.Distinct(StringComparer.Ordinal).Count() != topics.Length)
            throw new ArgumentException("Distinct, non-empty Kafka topics are required.", nameof(options));
        _options = new KafkaConsumerOptions { GroupId = options.GroupId, Topics = topics };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Native Consume is synchronous. Do not block host startup waiting for its first poll.
        await Task.Yield();
        var consumer = _consumerFactory(_options,
            reason => _logger.LogError("Kafka consumer error: {Reason}", reason));
        Exception? executionFailure = null;
        try
        {
            consumer.SubscribeTopics(_options.Topics);
            _logger.LogInformation("Kafka consumer started for topics: {Topics}", string.Join(", ", _options.Topics));
            while (!stoppingToken.IsCancellationRequested)
            {
                KafkaRecord? record;
                try { record = consumer.Consume(TimeSpan.FromSeconds(1)); }
                catch (KafkaMessagingComponentException ex) when (ex.Code == KafkaMessagingErrorCode.ConsumeFailed)
                {
                    _logger.LogWarning(ex, "Kafka consume error, continuing");
                    continue;
                }
                if (record is null) continue;
                stoppingToken.ThrowIfCancellationRequested();

                DomainEventMessage? message;
                try { message = record.Value is null ? null : JsonSerializer.Deserialize<DomainEventMessage>(record.Value); }
                catch (JsonException ex)
                {
                    // Preserve the existing poison-message policy. A failed commit still stops the session.
                    _logger.LogError(ex, "Malformed Kafka message from {Topic}, skipping", record.Topic);
                    consumer.Commit(record);
                    continue;
                }
                if (message is null) { consumer.Commit(record); continue; }

                using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                Task? handlerTask = null;
                consumer.Pause();
                try
                {
                    handlerTask = _handler(message, processing.Token);
                    while (!handlerTask.IsCompleted)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        consumer.PollWhilePaused(TimeSpan.FromMilliseconds(200));
                    }
                    await handlerTask.ConfigureAwait(false);
                    stoppingToken.ThrowIfCancellationRequested();
                    consumer.Commit(record);
                    consumer.Resume();
                    _logger.LogDebug("Consumed and committed {EventType} from {Topic}", message.EventType, record.Topic);
                }
                catch (Exception failure)
                {
                    // Never consume/commit a later offset after failed work. Drain the cooperative handler
                    // before disposing its session; it must be idempotent and honor the supplied cancellation.
                    Exception? cancellationFailure = null;
                    try { processing.Cancel(); }
                    catch (Exception error) { cancellationFailure = error; }
                    if (handlerTask is not null)
                    {
                        try { await handlerTask.ConfigureAwait(false); }
                        catch { /* Preserve the original poll/handler/commit failure. */ }
                    }
                    if (cancellationFailure is not null)
                        throw new AggregateException(failure, cancellationFailure);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The interrupted record stays uncommitted for replay by the next session.
        }
        catch (Exception error)
        {
            executionFailure = error;
            throw;
        }
        finally
        {
            try { consumer.Dispose(); }
            catch (Exception cleanupError) when (executionFailure is not null)
            {
                throw new AggregateException(executionFailure, cleanupError);
            }
            _logger.LogInformation("Kafka consumer stopped");
        }
    }
}

public sealed class KafkaConsumerOptions
{
    public string GroupId { get; set; } = "nexaone-mes";
    public IReadOnlyList<string> Topics { get; set; } = new[] { "nexaone.events" };
}
