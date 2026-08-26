using NexusCom.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Diagnostics;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// Exposes the configured NexaOne database provider through the product readiness catalog.
/// A health probe opens a real provider connection, while diagnostics deliberately omit
/// connection strings, server names, database names, and provider exception messages.
/// </summary>
public sealed class NexaOneDatabaseProbe : IExternalDependencyProbe
{
    public const string DependencyId = "nexaone.database";

    private readonly IDatabaseProvider _provider;
    private readonly string _connectionString;

    public NexaOneDatabaseProbe(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _provider = dataSource.Provider
            ?? throw new ArgumentException("A database provider is required.", nameof(dataSource));
        _connectionString = string.IsNullOrWhiteSpace(dataSource.ConnectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(dataSource))
            : dataSource.ConnectionString;

        Descriptor = new ExternalDependencyDescriptor(
            DependencyId,
            "NexaOne database provider",
            "database",
            _provider.GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            BuildCapabilities(_provider));
    }

    public ExternalDependencyDescriptor Descriptor { get; }

    public async ValueTask<ExternalDependencyHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await using var connection = _provider.CreateConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            return new ExternalDependencyHealth(
                ExternalDependencyHealthStatus.Healthy,
                $"Database provider '{_provider.Kind}' accepted an open connection.",
                checkedAtUtc,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["providerKind"] = _provider.Kind.ToString(),
                    ["supportsTransactions"] = _provider.Capabilities.SupportsTransactions.ToString(),
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
                $"Database connection validation failed ({error.GetType().Name}).",
                checkedAtUtc,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["providerKind"] = _provider.Kind.ToString(),
                    ["exceptionType"] = error.GetType().FullName ?? error.GetType().Name,
                });
        }
    }

    private static IReadOnlyList<string> BuildCapabilities(IDatabaseProvider provider)
    {
        var capabilities = new List<string> { "connection-open", "query" };
        if (provider.Capabilities.SupportsParameterizedCommands) capabilities.Add("parameterized-command");
        if (provider.Capabilities.SupportsTransactions) capabilities.Add("transaction");
        if (provider.Capabilities.SupportsStreaming) capabilities.Add("streaming");
        if (provider.Capabilities.SupportsCdc || provider.Capabilities.SupportsNotifications)
            capabilities.Add("change-feed");
        return capabilities;
    }
}
