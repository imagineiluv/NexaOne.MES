using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>Lazy parent proxy for the SYS-owned released program artifact directory.</summary>
public sealed class ReleasedProgramArtifactDirectoryProxy : IReleasedProgramArtifactDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public ReleasedProgramArtifactDirectoryProxy(ModuleBeanResolver resolver) =>
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<ReleasedProgramArtifactDto?> FindAsync(
        string artifactId,
        CancellationToken ct = default) =>
        _resolver.Resolve<IReleasedProgramArtifactDirectory>(
                "Sys",
                "releasedProgramArtifactDirectory")
            .FindAsync(artifactId, ct);
}
