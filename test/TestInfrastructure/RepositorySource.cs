using System.Reflection;

namespace NexaOne.Testing;

/// <summary>
/// Resolves source-owned test assets independently from the test output location.
/// The build-recorded checkout is authoritative; process locations are validated fallbacks only.
/// </summary>
internal static class RepositorySource
{
    private const string RepositoryRootMetadataKey = "NexaOne.RepositorySourceRoot";
    private static readonly Lazy<string> RepositoryRoot = new(ResolveRepositoryRoot);

    public static string Root => RepositoryRoot.Value;

    public static string GetFile(params string[] relativeSegments)
        => ResolveExisting(relativeSegments, File.Exists, "file");

    public static string GetDirectory(params string[] relativeSegments)
        => ResolveExisting(relativeSegments, Directory.Exists, "directory");

    internal static string ResolveRepositoryRoot(
        string? configuredRoot,
        params string?[] fallbackSearchRoots)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var authoritativeRoot = Path.GetFullPath(configuredRoot);
            if (IsRepositoryRoot(authoritativeRoot))
                return authoritativeRoot;

            throw new DirectoryNotFoundException(
                $"Build-recorded NexaOne repository root is invalid: '{authoritativeRoot}'.");
        }

        foreach (var fallbackRoot in fallbackSearchRoots)
        {
            var repositoryRoot = FindValidatedAncestor(fallbackRoot);
            if (repositoryRoot is not null)
                return repositoryRoot;
        }

        throw new DirectoryNotFoundException(
            "NexaOne repository root was not found in build metadata or validated process-location fallbacks.");
    }

    internal static bool IsRepositoryRoot(string candidate)
        => File.Exists(Path.Combine(candidate, "NexaOne.sln"))
           && File.Exists(Path.Combine(
               candidate,
               "src",
               "00.Main",
               "NexaOne.Server",
               "NexaOne.Server.csproj"))
           && File.Exists(Path.Combine(
               candidate,
               "test",
               "NexaOne.UnitTests",
               "NexaOne.UnitTests.csproj"));

    private static string ResolveRepositoryRoot()
    {
        var configuredRoot = typeof(RepositorySource).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(static metadata => metadata.Key == RepositoryRootMetadataKey)
            ?.Value;

        return ResolveRepositoryRoot(
            configuredRoot,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory());
    }

    private static string? FindValidatedAncestor(string? searchRoot)
    {
        if (string.IsNullOrWhiteSpace(searchRoot))
            return null;

        for (var directory = new DirectoryInfo(Path.GetFullPath(searchRoot));
             directory is not null;
             directory = directory.Parent)
        {
            if (IsRepositoryRoot(directory.FullName))
                return directory.FullName;
        }

        return null;
    }

    private static string ResolveExisting(
        IReadOnlyCollection<string> relativeSegments,
        Func<string, bool> exists,
        string pathKind)
    {
        if (relativeSegments.Count == 0
            || relativeSegments.Any(static segment => string.IsNullOrWhiteSpace(segment)))
        {
            throw new ArgumentException("A non-empty repository-relative path is required.", nameof(relativeSegments));
        }

        var normalizedSegments = relativeSegments
            .Select(static segment => segment
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar))
            .ToArray();
        var candidate = Path.GetFullPath(Path.Combine([Root, .. normalizedSegments]));
        var rootPrefix = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, pathComparison))
        {
            throw new InvalidOperationException(
                $"Repository-relative path escapes the checkout root: '{candidate}'.");
        }

        if (!exists(candidate))
            throw new FileNotFoundException($"Repository {pathKind} not found: '{candidate}'.", candidate);

        return candidate;
    }
}
