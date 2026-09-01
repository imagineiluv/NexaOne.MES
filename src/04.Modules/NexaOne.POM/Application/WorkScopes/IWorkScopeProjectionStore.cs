using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>
/// Durable projection application state and the POM aggregate mutation boundary. A claim is only
/// advisory until <see cref="CommitDecisionAsync"/> revalidates its lease, current-event cursor,
/// and aggregate version in one serializable transaction.
/// </summary>
internal interface IWorkScopeProjectionStore
{
    /// <summary>
    /// Verifies the durable projection schema and the worker's required read/write permissions
    /// without changing operational data. Hosted startup must await this probe before readiness.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    Task<WorkScopeProjectionClaim?> TryClaimNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<WorkScopeProjectionCommitResult> CommitDecisionAsync(
        WorkScopeProjectionClaim claim,
        PreparedWorkScopeProjectionDecision decision,
        CancellationToken ct = default);

    Task<WorkScopeProjectionCommitResult> RecordFailureAsync(
        WorkScopeProjectionClaim claim,
        WorkScopeProjectionPolicyIdentity policy,
        string errorCode,
        string errorMessage,
        bool quarantine,
        TimeSpan retryAfter,
        CancellationToken ct = default);
}

internal sealed record WorkScopeProjectionClaim(
    WorkScopeProjectionEventDto Event,
    WorkScopeDto WorkScope,
    string LeaseOwner,
    long LeaseFence,
    int AttemptCount,
    DateTimeOffset LeaseExpiresAt);

internal sealed record PreparedWorkScopeProjectionDecision(
    WorkScopeProjectionPolicyIdentity Policy,
    WorkScopeProjectionDecision Decision,
    string DecisionHash,
    string DecisionJson);

internal enum WorkScopeProjectionCommitKind
{
    Applied,
    Observed,
    RetryScheduled,
    Quarantined,
    Superseded,
    LeaseLost,
}

internal sealed record WorkScopeProjectionCommitResult(
    WorkScopeProjectionCommitKind Kind,
    string? Detail = null);
