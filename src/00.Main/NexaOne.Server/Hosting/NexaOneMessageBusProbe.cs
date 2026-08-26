using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Diagnostics;

namespace NexaOne.Server;

/// <summary>
/// Connects the Spring-owned message bus to the product readiness catalog. Resolution is
/// deferred until the MES runtime has started, preserving side-effect-free service registration.
/// </summary>
internal sealed class NexaOneMessageBusProbe : IExternalDependencyProbe
{
    public const string DependencyId = "nexaone.messaging";

    private readonly NexaOneMesRuntimeState _runtime;

    public NexaOneMessageBusProbe(NexaOneMesRuntimeState runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Descriptor = new ExternalDependencyDescriptor(
            DependencyId,
            "NexaOne message bus",
            "messaging",
            typeof(IMessageBus).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            ["event-publish", "outbox-dispatch", "transport-selection"]);
    }

    public ExternalDependencyDescriptor Descriptor { get; }

    public async ValueTask<ExternalDependencyHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAtUtc = DateTimeOffset.UtcNow;

        if (!_runtime.ModulesEnabled)
        {
            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Disabled,
                "MES modules are disabled; the Spring message bus was not started.",
                checkedAtUtc,
                new Dictionary<string, string> { ["transport"] = "none" });
        }

        if (!_runtime.IsInitialized)
        {
            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Unhealthy,
                "MES module runtime is not initialized and has not started, so the message bus cannot be resolved.",
                checkedAtUtc);
        }

        try
        {
            var messageBus = _runtime.GetInitializedServerBean<IMessageBus>("messageBus");
            if (messageBus is KafkaMessageBus kafkaMessageBus)
            {
                return await new KafkaBrokerProbe(kafkaMessageBus)
                    .CheckHealthAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (messageBus is InMemoryMessageBus)
            {
                return new ExternalDependencyHealth(
                    ExternalDependencyHealthStatus.Healthy,
                    "In-memory message bus is ready; delivery is limited to this process.",
                    checkedAtUtc,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["durability"] = "process-lifetime",
                        ["transport"] = "in-memory",
                    });
            }

            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Degraded,
                "A custom message bus is ready; transport-specific connectivity is not catalogued.",
                checkedAtUtc,
                new Dictionary<string, string> { ["transport"] = "custom" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Unhealthy,
                $"Spring bean 'messageBus' is unavailable ({error.GetType().Name}).",
                checkedAtUtc,
                new Dictionary<string, string>
                {
                    ["exceptionType"] = error.GetType().FullName ?? error.GetType().Name,
                });
        }
    }
}
