using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

internal interface IWorkScopeProjectionAuthorityRepository
{
    Task<WorkScopeProjectionAuthorityRecord?> GetByWorkScopeIdAsync(
        string workScopeId,
        CancellationToken ct = default);

    Task<WorkScopeProjectionAuthorityProvisionResult> ProvisionAsync(
        WorkScopeProjectionAuthorityEvidence evidence,
        string idempotencyKey,
        string requestHash,
        string actorId,
        CancellationToken ct = default);
}

internal enum WorkScopeProjectionAuthorityProvisionKind
{
    Provisioned,
    Replayed,
    ScopeNotFound,
    ScopeNotPristine,
    ScopeIdentityMismatch,
    IdempotencyConflict,
    EvidenceConflict,
    TrustedEvidenceMissing,
    TrustedEvidenceRevoked,
    RuntimeProductBindingMissing,
}

internal sealed record WorkScopeProjectionAuthorityProvisionResult(
    WorkScopeProjectionAuthorityProvisionKind Kind,
    WorkScopeProjectionAuthorityRecord? Authority);

internal sealed record WorkScopeProjectionAuthorityRecord(
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
    string ProvisionRequestHash,
    DateTime ProvisionedAt,
    string ProvisionedBy);
