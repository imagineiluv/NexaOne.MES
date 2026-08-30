namespace NexaOne.POM.Application.WorkScopes;

/// <summary>
/// Normalized equipment projection을 durable inbox/current cursor에 원자적으로 접수하는
/// application-owned output port입니다.
/// </summary>
internal interface IWorkScopeProjectionInbox
{
    Task<WorkScopeProjectionPersistResult> PersistAsync(
        WorkScopeProjectionEnvelope envelope,
        CancellationToken ct = default);
}

internal sealed record WorkScopeProjectionEnvelope(
    string SourceClientId,
    string EventId,
    string RequestHash,
    string WorkScopeId,
    string EquipmentId,
    string OperationKey,
    string PairRunId,
    string SequenceRunId,
    long SourceRevision,
    string ProjectionStatus,
    bool TerminalCleanupCompleted,
    string RecipeId,
    string RecipeSnapshotHash,
    string ProgramHash,
    IReadOnlyList<WorkScopeProjectionCarrierEnvelope> Carriers,
    string CarriersJson,
    string ResultCode,
    string? ResultMetadataJson,
    DateTime OccurredAt,
    string PayloadJson);

internal sealed record WorkScopeProjectionCarrierEnvelope(
    string Lane,
    string CarrierId,
    string CleaningRunId);

internal enum WorkScopeProjectionPersistKind
{
    Accepted,
    Replayed,
    EventHashConflict,
    SequenceIdentityConflict,
    WorkScopeBindingConflict,
    ScopeNotFound,
    ScopeEquipmentConflict,
}

internal sealed record WorkScopeProjectionPersistResult(
    WorkScopeProjectionPersistKind Kind,
    string SourceClientId,
    string EventId,
    string WorkScopeId,
    bool IsCurrent,
    long CurrentRevision,
    DateTime AcceptedAt);
