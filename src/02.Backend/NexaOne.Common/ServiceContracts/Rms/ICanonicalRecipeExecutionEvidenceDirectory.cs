namespace NexaOne.ServiceContracts.Rms;

/// <summary>
/// Read-only directory of immutable, canonical recipe evidence captured at execution time.
/// A legacy V113 row whose schema or hash is absent is deliberately not authoritative.
/// </summary>
public interface ICanonicalRecipeExecutionEvidenceDirectory
{
    Task<CanonicalRecipeExecutionEvidenceDto?> FindAsync(
        string executionId,
        CancellationToken ct = default);
}

public sealed record CanonicalRecipeExecutionEvidenceDto(
    string ExecutionId,
    string WorkScopeId,
    string PairRunId,
    string SequenceRunId,
    string EquipmentId,
    string OperationKey,
    string RecipeId,
    int RecipeVersion,
    string? SnapshotSchema,
    string? SnapshotHash,
    DateTime CapturedAt);
