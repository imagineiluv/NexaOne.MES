using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NexaOne.Infrastructure.Messaging;

public sealed class KafkaConsumerService : BackgroundService
{
    private readonly KafkaConsumerOptions _options;
    private readonly Func<DomainEventMessage, CancellationToken, Task> _handler;
    private readonly ILogger<KafkaConsumerService> _logger;

    public KafkaConsumerService(
        KafkaConsumerOptions options,
        Func<DomainEventMessage, CancellationToken, Task> handler,
        ILogger<KafkaConsumerService> logger)
    {
        _options = options;
        _handler = handler;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers  = _options.BootstrapServers,
            GroupId           = _options.GroupId,
            AutoOffsetReset   = AutoOffsetReset.Earliest,
            EnableAutoCommit  = false,
            SessionTimeoutMs  = 10_000,
            MaxPollIntervalMs = 300_000
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Kafka consumer error: {Reason}", e.Reason))
            .Build();

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

                try
                {
                    var message = JsonSerializer.Deserialize<DomainEventMessage>(result.Message.Value);
                    if (message is not null)
                    {
                        await _handler(message, stoppingToken);
                        consumer.Commit(result);
                        _logger.LogDebug("Consumed and committed {EventType} from {Topic}",
                            message.EventType, result.Topic);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling Kafka message from {Topic}", result.Topic);
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
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string GroupId { get; set; } = "nexaone-mes";
    public IReadOnlyList<string> Topics { get; set; } = new[] { "nexaone.events" };
}
