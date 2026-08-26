namespace NexaOne.Infrastructure.Diagnostics;

/// <summary>Describes the readiness of a product-owned external dependency.</summary>
public enum ExternalDependencyHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Disabled = 4,
}

/// <summary>Immutable product metadata for one external dependency probe.</summary>
public sealed class ExternalDependencyDescriptor
{
    public ExternalDependencyDescriptor(
        string id,
        string displayName,
        string kind,
        string version,
        IEnumerable<string> capabilities,
        bool requiredAtStartup = true)
    {
        if (string.IsNullOrWhiteSpace(id) || !StringComparer.Ordinal.Equals(id, id.Trim()))
            throw new ArgumentException("A stable, trimmed dependency ID is required.", nameof(id));
        if (id.Length > 128 || id.Any(char.IsControl))
            throw new ArgumentException("Dependency IDs cannot exceed 128 characters or contain control characters.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("A dependency kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A dependency version is required.", nameof(version));
        ArgumentNullException.ThrowIfNull(capabilities);

        var capabilityArray = capabilities.ToArray();
        if (capabilityArray.Any(string.IsNullOrWhiteSpace)
            || capabilityArray.Distinct(StringComparer.Ordinal).Count() != capabilityArray.Length)
        {
            throw new ArgumentException(
                "Dependency capabilities must be nonblank and unique.",
                nameof(capabilities));
        }

        Id = id;
        DisplayName = displayName;
        Kind = kind;
        Version = version;
        Capabilities = Array.AsReadOnly(capabilityArray);
        RequiredAtStartup = requiredAtStartup;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Kind { get; }
    public string Version { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public bool RequiredAtStartup { get; }
}

/// <summary>One secret-free readiness observation produced by a product probe.</summary>
public sealed class ExternalDependencyHealth
{
    public ExternalDependencyHealth(
        ExternalDependencyHealthStatus status,
        string summary,
        DateTimeOffset checkedAtUtc,
        IReadOnlyDictionary<string, string>? details = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("A health summary is required.", nameof(summary));

        Status = status;
        Summary = summary;
        CheckedAtUtc = checkedAtUtc.ToUniversalTime();
        Details = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(details ?? new Dictionary<string, string>(), StringComparer.Ordinal));
    }

    public ExternalDependencyHealthStatus Status { get; }
    public string Summary { get; }
    public DateTimeOffset CheckedAtUtc { get; }
    public IReadOnlyDictionary<string, string> Details { get; }
}

/// <summary>A dependency descriptor paired with its latest on-demand observation.</summary>
public sealed record ExternalDependencySnapshot(
    ExternalDependencyDescriptor Descriptor,
    ExternalDependencyHealth Health);

/// <summary>
/// Product-owned readiness seam. Implementations probe external state but do not claim ownership of
/// a protocol lifecycle merely because they inspect a database, broker, or protocol catalog.
/// </summary>
public interface IExternalDependencyProbe
{
    ExternalDependencyDescriptor Descriptor { get; }

    ValueTask<ExternalDependencyHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates and evaluates the complete external dependency plan behind one small interface.
/// </summary>
public sealed class ExternalDependencyProbeCatalog
{
    private readonly IReadOnlyList<IExternalDependencyProbe> _probes;

    public ExternalDependencyProbeCatalog(IEnumerable<IExternalDependencyProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var probeArray = probes.ToArray();
        var duplicate = probeArray
            .GroupBy(static probe => probe.Descriptor.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate external dependency ID '{duplicate.Key}'.");

        _probes = Array.AsReadOnly(probeArray);
        Descriptors = Array.AsReadOnly(probeArray.Select(static probe => probe.Descriptor).ToArray());
    }

    public IReadOnlyList<ExternalDependencyDescriptor> Descriptors { get; }

    public async Task<IReadOnlyList<ExternalDependencySnapshot>> CheckAllAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshots = new ExternalDependencySnapshot[_probes.Count];
        for (var index = 0; index < _probes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = _probes[index];
            var health = await probe.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            snapshots[index] = new ExternalDependencySnapshot(probe.Descriptor, health);
        }

        return Array.AsReadOnly(snapshots);
    }
}
