using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Infrastructure;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM;

/// <summary>
/// Optional application-side composition for interpreting durable projection evidence.
/// Core POM acceptance and schema registration deliberately remain in <see cref="Module"/>.
/// </summary>
public sealed class WorkScopeProjectionApplicationModule
{
    private readonly WorkScopeProjectionWorker _runtime;

    public WorkScopeProjectionApplicationModule(
        EesDataSource dataSource,
        IConfiguration configuration,
        IWorkScopeProjectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(policy);

        var options = PomProjectionOptions.FromConfiguration(configuration);
        _runtime = new WorkScopeProjectionWorker(
            new WorkScopeProjectionProcessor(
                new WorkScopeProjectionStore(dataSource),
                policy,
                TimeSpan.FromSeconds(options.LeaseDurationSeconds)),
            enabled: options.Enabled,
            leaseOwner: options.LeaseOwner,
            pollInterval: TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds),
            batchSize: options.BatchSize);
    }

    /// <summary>Exposes the Default-ALC marker used for fail-fast optional-feature validation.</summary>
    public IWorkScopeProjectionRuntime GetWorkScopeProjectionRuntime() => _runtime;

    /// <summary>Exposes the same singleton as the hosted-service lifecycle participant.</summary>
    public IHostedService GetWorkScopeProjectionWorker() => _runtime;
}

/// <summary>Durable WorkScope projection worker 설정의 안전한 범위를 한곳에서 정규화합니다.</summary>
internal sealed record PomProjectionOptions(
    bool Enabled,
    string? LeaseOwner,
    int LeaseDurationSeconds,
    int PollIntervalMilliseconds,
    int BatchSize)
{
    public static PomProjectionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var owner = configuration["Worker:Pom:WorkScopeProjection:LeaseOwner"]?.Trim();
        return new PomProjectionOptions(
            Enabled: configuration.GetValue("Worker:Pom:WorkScopeProjection:Enabled", false),
            LeaseOwner: string.IsNullOrWhiteSpace(owner) ? null : owner,
            LeaseDurationSeconds: Math.Clamp(
                configuration.GetValue("Worker:Pom:WorkScopeProjection:LeaseDurationSeconds", 120),
                5,
                900),
            PollIntervalMilliseconds: Math.Clamp(
                configuration.GetValue("Worker:Pom:WorkScopeProjection:PollIntervalMilliseconds", 2_000),
                100,
                300_000),
            BatchSize: Math.Clamp(
                configuration.GetValue("Worker:Pom:WorkScopeProjection:BatchSize", 50),
                1,
                500));
    }
}
