using NexaOne.Common;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// Provisions the immutable authority that lets one equipment projection stream own a pristine
/// WorkScope. The command deliberately carries references only: recipe/program hashes are accepted
/// only from <see cref="IWorkScopeProjectionAuthorityValidator"/> after it resolves authoritative
/// RMS execution and released program artifacts.
/// </summary>
public interface IWorkScopeProjectionAuthorityBridge : INexaModuleBridge
{
    Task<Result<WorkScopeProjectionAuthorityDto>> ProvisionAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default);

    Task<Result<WorkScopeProjectionAuthorityDto>> GetAsync(
        string workScopeId,
        CancellationToken ct = default);
}

public sealed record WorkScopeProjectionAuthorityProvisionCommand(
    string WorkScopeId,
    string SourceClientId,
    string EquipmentId,
    string OperationKey,
    string PairRunId,
    string SequenceRunId,
    string RecipeExecutionId,
    string ProgramArtifactId,
    string IdempotencyKey,
    string ActorId);

/// <summary>
/// Trusted coordinator seam. Implementations must resolve immutable source records and return the
/// exact authority evidence; they must not echo hashes supplied by an API caller.
/// </summary>
public interface IWorkScopeProjectionAuthorityValidator
{
    Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default);
}

public sealed record WorkScopeProjectionAuthorityEvidence(
    string WorkScopeId,
    string SourceClientId,
    string EquipmentId,
    string OperationKey,
    string PairRunId,
    string SequenceRunId,
    string RecipeExecutionId,
    string RecipeId,
    int RecipeVersion,
    string RecipeSnapshotSchema,
    string RecipeSnapshotHash,
    string ProgramArtifactId,
    string ProgramSchema,
    string ProgramHash);

public sealed record WorkScopeProjectionAuthorityDto(
    string WorkScopeId,
    string SourceClientId,
    string EquipmentId,
    string OperationKey,
    string PairRunId,
    string SequenceRunId,
    string RecipeExecutionId,
    string RecipeId,
    int RecipeVersion,
    string RecipeSnapshotSchema,
    string RecipeSnapshotHash,
    string ProgramArtifactId,
    string ProgramSchema,
    string ProgramHash,
    int BaselineVersionNo,
    int LastAppliedVersionNo,
    string ProvisionIdempotencyKey,
    DateTime ProvisionedAt,
    string ProvisionedBy,
    bool Replay);
