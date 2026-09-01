using NexaOne.ServiceContracts.Rms;

namespace NexaOne.Server.Gateway;

/// <summary>Lazy parent proxy for the RMS-owned canonical recipe execution directory.</summary>
public sealed class CanonicalRecipeExecutionEvidenceDirectoryProxy
    : ICanonicalRecipeExecutionEvidenceDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public CanonicalRecipeExecutionEvidenceDirectoryProxy(ModuleBeanResolver resolver) =>
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<CanonicalRecipeExecutionEvidenceDto?> FindAsync(
        string executionId,
        CancellationToken ct = default) =>
        _resolver.Resolve<ICanonicalRecipeExecutionEvidenceDirectory>(
                "Rms",
                "canonicalRecipeExecutionEvidenceDirectory")
            .FindAsync(executionId, ct);
}
