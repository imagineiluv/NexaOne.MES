namespace NexaOne.ServiceContracts.Sys;

/// <summary>
/// Append-only release evidence for an executable product program. Revocation is a separate event:
/// it prevents new authority while preserving the artifact identity used by existing execution/replay.
/// </summary>
public interface IReleasedProgramArtifactDirectory
{
    Task<ReleasedProgramArtifactDto?> FindAsync(
        string artifactId,
        CancellationToken ct = default);
}

public sealed record ReleasedProgramArtifactDto(
    string ArtifactId,
    string EquipmentId,
    string OperationKey,
    string ProductProfileId,
    string PluginId,
    string ProductDefinitionVersion,
    string ProgramVersion,
    string ProgramSchema,
    string ProgramHash,
    string BoundRecipeSnapshotSchema,
    string BoundRecipeSnapshotHash,
    DateTime ReleasedAt,
    string ReleasedBy,
    ProgramArtifactRevocationDto? Revocation = null)
{
    public bool IsRevoked => Revocation is not null;
}

public sealed record ProgramArtifactRevocationDto(
    string RevocationId,
    string ArtifactId,
    DateTime RevokedAt,
    string RevokedBy,
    string Reason);
