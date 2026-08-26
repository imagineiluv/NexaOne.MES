using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexaOne.Infrastructure.Diagnostics;

namespace NexaOne.Server;

/// <summary>
/// Reports readiness from the same external-dependency catalog used by startup validation. Disabled
/// dependencies are intentional configuration choices; an unhealthy required dependency makes the host
/// unready, while unknown or degraded required state is surfaced as degraded.
/// </summary>
internal sealed class ExternalDependencyHealthCheck : IHealthCheck
{
    private readonly ExternalDependencyProbeCatalog _dependencies;

    public ExternalDependencyHealthCheck(ExternalDependencyProbeCatalog dependencies)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var required = (await _dependencies.CheckAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(static snapshot => snapshot.Descriptor.RequiredAtStartup)
            .ToArray();
        var unhealthyIds = required
            .Where(static snapshot => snapshot.Health.Status == ExternalDependencyHealthStatus.Unhealthy)
            .Select(static snapshot => snapshot.Descriptor.Id)
            .ToArray();
        if (unhealthyIds.Length > 0)
        {
            return HealthCheckResult.Unhealthy(
                "Required external dependencies are unhealthy: " + string.Join(", ", unhealthyIds));
        }

        var degradedIds = required
            .Where(static snapshot => snapshot.Health.Status is
                ExternalDependencyHealthStatus.Degraded or ExternalDependencyHealthStatus.Unknown)
            .Select(static snapshot => snapshot.Descriptor.Id)
            .ToArray();
        if (degradedIds.Length > 0)
        {
            return HealthCheckResult.Degraded(
                "Required external dependencies are not fully healthy: " + string.Join(", ", degradedIds));
        }

        return HealthCheckResult.Healthy("All enabled required external dependencies are ready.");
    }
}
