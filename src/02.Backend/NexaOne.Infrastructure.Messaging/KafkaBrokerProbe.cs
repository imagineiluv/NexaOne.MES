using NexaDB.Messaging.Kafka;
using NexaOne.Infrastructure.Diagnostics;

namespace NexaOne.Infrastructure.Messaging;

/// <summary>
/// Readiness probe for the concrete Kafka message bus. Resolving the bus may be deferred so
/// composition roots such as Spring can finish their lifecycle before startup diagnostics run.
/// Healthy/Ready requires a real broker metadata response; producer construction alone is not
/// treated as connectivity evidence.
/// </summary>
public sealed class KafkaBrokerProbe : IExternalDependencyProbe
{
    public const string DependencyId = "nexaone.messaging.kafka";

    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);
    private readonly Func<CancellationToken, ValueTask> _probeBroker;

    public KafkaBrokerProbe(KafkaMessageBus messageBus)
        : this(CreateBrokerProbe(messageBus))
    {
    }

    public KafkaBrokerProbe(Func<KafkaMessageBus> resolveBus)
        : this(CreateBrokerProbe(resolveBus))
    {
    }

    internal KafkaBrokerProbe(Func<CancellationToken, ValueTask> probeBroker)
    {
        _probeBroker = probeBroker ?? throw new ArgumentNullException(nameof(probeBroker));
        Descriptor = new ExternalDependencyDescriptor(
            DependencyId,
            "NexaOne Kafka message bus",
            "messaging",
            typeof(KafkaDriver).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            ["event-publish", "kafka-produce", "producer-acks-all", "producer-idempotence"]);
    }

    public ExternalDependencyDescriptor Descriptor { get; }

    private static Func<CancellationToken, ValueTask> CreateBrokerProbe(KafkaMessageBus messageBus)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        return cancellationToken => messageBus.ProbeBrokerAsync(DefaultProbeTimeout, cancellationToken);
    }

    private static Func<CancellationToken, ValueTask> CreateBrokerProbe(
        Func<KafkaMessageBus> resolveBus)
    {
        ArgumentNullException.ThrowIfNull(resolveBus);
        return cancellationToken =>
        {
            var messageBus = resolveBus()
                ?? throw new InvalidOperationException("The Kafka message bus resolver returned null.");
            return messageBus.ProbeBrokerAsync(DefaultProbeTimeout, cancellationToken);
        };
    }

    public async ValueTask<ExternalDependencyHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await _probeBroker(cancellationToken).ConfigureAwait(false);

            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Healthy,
                "Kafka broker metadata probe succeeded.",
                checkedAtUtc,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["brokerProbe"] = "metadata",
                    ["transport"] = "kafka",
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Unhealthy,
                $"Kafka broker metadata probe failed ({error.GetType().Name}).",
                checkedAtUtc,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = error.GetType().FullName ?? error.GetType().Name,
                    ["transport"] = "kafka",
                });
        }
    }
}
