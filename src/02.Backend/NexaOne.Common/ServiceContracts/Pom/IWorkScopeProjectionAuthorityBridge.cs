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
/// Legacy trusted coordinator seam. This exact method contract is retained for source and binary
/// compatibility with validators compiled before the contract-owned V2 decision was introduced.
/// </summary>
public interface IWorkScopeProjectionAuthorityValidator
{
    Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default);
}

/// <summary>
/// Trusted coordinator seam for project validators. Implementations resolve immutable source
/// records and return a contract-owned decision without exposing Result/Error as their outcome.
/// </summary>
public interface IWorkScopeProjectionAuthorityValidatorV2
{
    Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default);
}

/// <summary>
/// Contract-owned validator outcome. It deliberately does not expose the application's
/// <c>Result</c>/<c>Error</c> implementation across the project-plugin boundary.
/// </summary>
public sealed record WorkScopeProjectionAuthorityValidationDecision
{
    private WorkScopeProjectionAuthorityValidationDecision(
        bool isAccepted,
        WorkScopeProjectionAuthorityEvidence? evidence,
        string? rejectionCode,
        string? rejectionMessage)
    {
        IsAccepted = isAccepted;
        Evidence = evidence;
        RejectionCode = rejectionCode;
        RejectionMessage = rejectionMessage;
    }

    public bool IsAccepted { get; }
    public WorkScopeProjectionAuthorityEvidence? Evidence { get; }
    public string? RejectionCode { get; }
    public string? RejectionMessage { get; }

    public static WorkScopeProjectionAuthorityValidationDecision Accepted(
        WorkScopeProjectionAuthorityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new(true, evidence, null, null);
    }

    public static WorkScopeProjectionAuthorityValidationDecision Rejected(
        string code,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(false, null, code, message);
    }
}

/// <summary>Exact-key, read-only WorkScope evidence used by trusted authority validators.</summary>
public interface IWorkScopeAuthorityEvidenceDirectory
{
    Task<WorkScopeDto?> FindAsync(string workScopeId, CancellationToken ct = default);
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
