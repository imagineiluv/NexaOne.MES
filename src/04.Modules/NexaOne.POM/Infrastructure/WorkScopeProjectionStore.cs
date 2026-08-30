using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// Durable WorkScope projection consumer. Claims are short transactions; project policy evaluation
/// occurs outside this adapter, and every decision is committed under serializable lease, current
/// event, aggregate version, execution idempotency, and application-state fences.
/// </summary>
internal sealed class WorkScopeProjectionStore : IWorkScopeProjectionStore
{
    private const int MaxSqliteBusyRetries = 6;
    private const int MaxSqlServerDeadlockRetries = 3;
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _isSqlServer;

    public WorkScopeProjectionStore(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
    }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        await RetryBusyAsync(
            () => _processor.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    // Bind every table/column used by claim, snapshot and fenced commit before the
                    // host advertises readiness. Each DML probe is deliberately guaranteed to
                    // affect zero rows, but still requires the same UPDATE/INSERT permissions as
                    // the live worker path.
                    await ExecuteProbeAsync(
                        connection, transaction, ReadinessSelectSql, ct).ConfigureAwait(false);
                    await ExecuteProbeAsync(
                        connection, transaction, ReadinessApplicationUpdateSql, ct)
                        .ConfigureAwait(false);
                    await ExecuteProbeAsync(
                        connection, transaction, ReadinessScopeUpdateSql, ct).ConfigureAwait(false);
                    await ExecuteProbeAsync(
                        connection, transaction, ReadinessApplicationEventInsertSql, ct)
                        .ConfigureAwait(false);
                    await ExecuteProbeAsync(
                        connection, transaction, ReadinessExecutionInsertSql, ct)
                        .ConfigureAwait(false);

                    var uniqueBindingIndex = await connection.ExecuteScalarAsync<long>(
                        new CommandDefinition(
                            _isSqlServer
                                ? ReadinessUniqueBindingIndexSqlServer
                                : ReadinessUniqueBindingIndexSqlite,
                            transaction: transaction,
                            cancellationToken: ct)).ConfigureAwait(false);
                    if (uniqueBindingIndex != 1)
                    {
                        throw new InvalidOperationException(
                            "WorkScope projection readiness failed: the unique current WorkScope "
                            + "binding index from V157 is missing or disabled.");
                    }

                    return true;
                },
                IsolationLevel.ReadCommitted,
                ct),
            ct).ConfigureAwait(false);
    }

    public async Task<WorkScopeProjectionClaim?> TryClaimNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var owner = leaseOwner?.Trim() ?? string.Empty;
        if (owner.Length is < 1 or > 200)
            throw new ArgumentException("A lease owner up to 200 characters is required.", nameof(leaseOwner));
        var duration = leaseDuration < TimeSpan.FromSeconds(5)
            ? TimeSpan.FromSeconds(5)
            : leaseDuration > TimeSpan.FromMinutes(15)
                ? TimeSpan.FromMinutes(15)
                : leaseDuration;
        var lease = await RetryBusyAsync(
            () => _processor.ExecuteInTransactionAsync(
                (connection, transaction) => ClaimCoreAsync(
                    connection, transaction, owner, duration, ct),
                // SQL Server permits READPAST only at ReadCommitted/RepeatableRead. The
                // UPDLOCK+READPAST candidate row and fenced conditional UPDATE provide the
                // queue-claim serialization; SQLite keeps its single-writer transaction.
                _isSqlServer ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                ct),
            ct).ConfigureAwait(false);
        if (lease is null) return null;

        // Do not load the WorkScope while holding the application-row claim lock. The second,
        // read-only transaction snapshots immutable evidence + current aggregate state; commit
        // later revalidates both current event and aggregate version under the global lock order.
        return await RetryBusyAsync(
            () => _processor.ExecuteInTransactionAsync(
                (connection, transaction) => LoadClaimSnapshotAsync(
                    connection, transaction, lease, ct),
                IsolationLevel.ReadCommitted,
                ct),
            ct).ConfigureAwait(false);
    }

    public Task<WorkScopeProjectionCommitResult> CommitDecisionAsync(
        WorkScopeProjectionClaim claim,
        PreparedWorkScopeProjectionDecision decision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(decision);
        return RetryBusyAsync(
            () => _processor.ExecuteInTransactionAsync(
                (connection, transaction) => decision.Decision.Disposition == WorkScopeProjectionDisposition.Apply
                    ? ApplyCoreAsync(connection, transaction, claim, decision, ct)
                    : FinalizeCoreAsync(
                        connection,
                        transaction,
                        claim,
                        decision.Decision.Disposition switch
                        {
                            WorkScopeProjectionDisposition.Observe => "Observed",
                            WorkScopeProjectionDisposition.Retry => "Retry",
                            WorkScopeProjectionDisposition.Quarantine => "Quarantined",
                            _ => throw new InvalidOperationException(
                                $"Unsupported projection disposition '{decision.Decision.Disposition}'."),
                        },
                        decision.Policy,
                        decision.DecisionHash,
                        decision.DecisionJson,
                        decision.Decision.Disposition is WorkScopeProjectionDisposition.Retry
                            or WorkScopeProjectionDisposition.Quarantine
                            ? decision.Decision.ReasonCode
                            : null,
                        decision.Decision.Disposition is WorkScopeProjectionDisposition.Retry
                            or WorkScopeProjectionDisposition.Quarantine
                            ? decision.Decision.ReasonCode
                            : null,
                        decision.Decision.RetryAfter is { } requested
                            ? WorkScopeProjectionProcessor.BoundedRetry(requested)
                            : TimeSpan.Zero,
                        ct),
                IsolationLevel.Serializable,
                ct),
            ct);
    }

    public Task<WorkScopeProjectionCommitResult> RecordFailureAsync(
        WorkScopeProjectionClaim claim,
        WorkScopeProjectionPolicyIdentity policy,
        string errorCode,
        string errorMessage,
        bool quarantine,
        TimeSpan retryAfter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(policy);
        var code = Required(errorCode, 100, nameof(errorCode));
        var message = Required(errorMessage, 2_000, nameof(errorMessage));
        return RetryBusyAsync(
            () => _processor.ExecuteInTransactionAsync(
                (connection, transaction) => FinalizeCoreAsync(
                    connection,
                    transaction,
                    claim,
                    quarantine ? "Quarantined" : "Retry",
                    policy,
                    decisionHash: null,
                    decisionJson: null,
                    code,
                    message,
                    WorkScopeProjectionProcessor.BoundedRetry(retryAfter),
                    ct),
                IsolationLevel.Serializable,
                ct),
            ct);
    }

    private async Task<ClaimLeaseRow?> ClaimCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        var now = await ReadDatabaseUtcAsync(connection, transaction, ct).ConfigureAwait(false);
        var candidate = await QueryFirstOrDefaultAsync<CandidateRow>(
            connection,
            transaction,
            _isSqlServer ? CandidateSqlSqlServer : CandidateSqlSqlite,
            new { Now = now },
            ct).ConfigureAwait(false);
        if (candidate is null) return null;

        var leaseExpiresAt = now.Add(leaseDuration);
        var claimed = await ExecuteAsync(connection, transaction, ClaimSql, new
        {
            candidate.SourceClientId,
            candidate.EventId,
            FromStatus = candidate.ApplicationStatus,
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
            Now = now,
        }, ct).ConfigureAwait(false);
        if (claimed != 1) return null;

        var lease = await QueryFirstOrDefaultAsync<ClaimLeaseRow>(
            connection,
            transaction,
            ClaimedApplicationSql,
            new { candidate.SourceClientId, candidate.EventId, LeaseOwner = leaseOwner },
            ct).ConfigureAwait(false)
            ?? throw new DBConcurrencyException("Claimed projection application disappeared.");
        await InsertAuditAsync(
            connection,
            transaction,
            lease.SourceClientId,
            lease.EventId,
            "Processing",
            candidate.ApplicationStatus,
            "Processing",
            lease.AttemptCount,
            lease.LeaseFence,
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            ct).ConfigureAwait(false);
        return lease;
    }

    private async Task<WorkScopeProjectionClaim> LoadClaimSnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClaimLeaseRow lease,
        CancellationToken ct)
    {
        var row = await QueryFirstOrDefaultAsync<ClaimRow>(
            connection,
            transaction,
            ClaimSnapshotSql,
            new { lease.SourceClientId, lease.EventId },
            ct).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Projection evidence '{lease.SourceClientId}/{lease.EventId}' has no WorkScope snapshot.");
        var carriers = (await connection.QueryAsync<CarrierEvidenceRow>(new CommandDefinition(
            CarrierEvidenceSql,
            new { lease.SourceClientId, lease.EventId },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        if (carriers.Count != 2)
            throw new InvalidDataException(
                $"Projection evidence '{lease.SourceClientId}/{lease.EventId}' must have exactly two normalized carriers.");
        return row.ToClaim(lease, carriers);
    }

    private async Task<WorkScopeProjectionCommitResult> ApplyCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionClaim claim,
        PreparedWorkScopeProjectionDecision prepared,
        CancellationToken ct)
    {
        var now = await ReadDatabaseUtcAsync(connection, transaction, ct).ConfigureAwait(false);

        // Keep the lock order compatible with ingestion: scope -> current -> application.
        var scopeRow = await QueryFirstOrDefaultAsync<ScopeRow>(
            connection,
            transaction,
            _isSqlServer ? ScopeForUpdateSqlSqlServer : ScopeForUpdateSql,
            new { WorkScopeId = claim.Event.WorkScopeId },
            ct).ConfigureAwait(false);
        var current = await ReadCurrentAsync(connection, transaction, claim, ct).ConfigureAwait(false);
        var application = await ReadApplicationAsync(connection, transaction, claim, ct).ConfigureAwait(false);
        var authority = await ValidateAuthorityAsync(
            connection, transaction, claim, application, current, now, ct).ConfigureAwait(false);
        if (authority is not null) return authority;

        if (scopeRow is null
            || !string.Equals(scopeRow.WorkScopeId, claim.Event.WorkScopeId, StringComparison.Ordinal)
            || !string.Equals(scopeRow.EquipmentId, claim.Event.EquipmentId, StringComparison.Ordinal))
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Quarantined",
                prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
                "Projection.ScopeIdentityChanged",
                "The WorkScope no longer matches the immutable projection evidence.",
                TimeSpan.Zero, now, ct).ConfigureAwait(false);
        }

        if (scopeRow.VersionNo != claim.WorkScope.VersionNo)
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Retry",
                prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
                "Projection.WorkScopeVersionChanged",
                $"WorkScope version changed from {claim.WorkScope.VersionNo} to {scopeRow.VersionNo} after policy evaluation.",
                TimeSpan.FromSeconds(1), now, ct).ConfigureAwait(false);
        }

        var occurredAt = claim.Event.OccurredAt.UtcDateTime;
        if (occurredAt < AsUtc(scopeRow.CreatedAt))
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Quarantined",
                prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
                "Projection.OccurredBeforeScopeCreated",
                "Projection mutation time cannot precede the target WorkScope creation time.",
                TimeSpan.Zero, now, ct).ConfigureAwait(false);
        }

        if (scopeRow.StartedAt is { } startedAt
            && prepared.Decision.Effects.Any(static effect => effect.Action == WorkScopeAction.Complete)
            && occurredAt < AsUtc(startedAt))
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Quarantined",
                prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
                "Projection.CompletedBeforeScopeStarted",
                "Projection completion time cannot precede the target WorkScope start time.",
                TimeSpan.Zero, now, ct).ConfigureAwait(false);
        }

        var scope = scopeRow.ToDomain();
        var parentRejection = await ValidateParentAsync(
            connection, transaction, scope, prepared.Decision.Effects, ct).ConfigureAwait(false);
        if (parentRejection is not null)
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Quarantined",
                prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
                "Projection.ParentScopeRejected", parentRejection,
                TimeSpan.Zero, now, ct).ConfigureAwait(false);
        }

        var executionRows = new List<ExecutionRow>(prepared.Decision.Effects.Count);
        for (var ordinal = 0; ordinal < prepared.Decision.Effects.Count; ordinal++)
        {
            var effect = prepared.Decision.Effects[ordinal];
            var from = scope.Status;
            var result = Apply(scope, effect, claim.Event.OccurredAt.UtcDateTime);
            if (result.IsFailure)
            {
                return await TransitionAsync(
                    connection, transaction, claim, application!, "Quarantined",
                    prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
                    "Projection.DomainRejected", result.Error.Description,
                    TimeSpan.Zero, now, ct).ConfigureAwait(false);
            }

            var expectedVersion = scopeRow.VersionNo + ordinal;
            var identity = ProjectionIdentity.Execution(
                claim.Event.SourceClientId,
                claim.Event.EventId,
                prepared.Policy,
                ordinal);
            executionRows.Add(new ExecutionRow
            {
                ExecutionId = identity.ExecutionId,
                WorkScopeId = scope.Id,
                IdempotencyKey = identity.IdempotencyKey,
                Action = effect.Action.ToString(),
                FromStatus = from.ToString(),
                ToStatus = scope.Status.ToString(),
                GoodQty = effect.GoodQty,
                DefectQty = effect.DefectQty,
                UserId = "PROJECTION",
                EquipmentId = claim.Event.EquipmentId,
                ClientChannel = "MES",
                DeviceId = claim.Event.SourceClientId.Length <= 100
                    ? claim.Event.SourceClientId
                    : claim.Event.SourceClientId[..100],
                OccurredAt = claim.Event.OccurredAt.UtcDateTime,
                Remark = effect.Remark,
                ExpectedVersion = expectedVersion,
                ResultVersion = expectedVersion + 1,
                CarrierId = effect.CarrierId ?? scope.CarrierId,
                ResultCode = effect.ResultCode,
                ResultMetadataJson = effect.ResultMetadataJson,
            });
        }

        var updatedScope = await ExecuteAsync(connection, transaction, UpdateScopeSql, new
        {
            WorkScopeId = scope.Id,
            ExpectedVersion = scopeRow.VersionNo,
            EffectCount = executionRows.Count,
            Status = scope.Status.ToString(),
            IsHold = scope.IsHold ? "Y" : "N",
            scope.StartQty,
            scope.CompleteQty,
            scope.ScrapQty,
            scope.StartedAt,
            scope.CompletedAt,
            UpdatedBy = "PROJECTION",
            UpdatedAt = now,
        }, ct).ConfigureAwait(false);
        if (updatedScope != 1)
            throw new DBConcurrencyException("WorkScope optimistic version fence was lost during projection application.");

        foreach (var execution in executionRows)
        {
            var inserted = await ExecuteAsync(
                connection, transaction, InsertExecutionSql, execution, ct).ConfigureAwait(false);
            if (inserted != 1)
                throw new DBConcurrencyException("Projection execution ledger insert did not affect exactly one row.");
        }

        return await TransitionAsync(
            connection, transaction, claim, application!, "Applied",
            prepared.Policy, prepared.DecisionHash, prepared.DecisionJson,
            null, null, TimeSpan.Zero, now, ct).ConfigureAwait(false);
    }

    private async Task<WorkScopeProjectionCommitResult> FinalizeCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionClaim claim,
        string targetStatus,
        WorkScopeProjectionPolicyIdentity policy,
        string? decisionHash,
        string? decisionJson,
        string? errorCode,
        string? errorMessage,
        TimeSpan retryAfter,
        CancellationToken ct)
    {
        var now = await ReadDatabaseUtcAsync(connection, transaction, ct).ConfigureAwait(false);
        var scope = await QueryFirstOrDefaultAsync<ScopeRow>(
            connection,
            transaction,
            _isSqlServer ? ScopeForUpdateSqlSqlServer : ScopeForUpdateSql,
            new { WorkScopeId = claim.Event.WorkScopeId },
            ct).ConfigureAwait(false);
        var current = await ReadCurrentAsync(connection, transaction, claim, ct).ConfigureAwait(false);
        var application = await ReadApplicationAsync(connection, transaction, claim, ct).ConfigureAwait(false);
        var authority = await ValidateAuthorityAsync(
            connection, transaction, claim, application, current, now, ct).ConfigureAwait(false);
        if (authority is not null) return authority;

        if (scope is null
            || !string.Equals(scope.WorkScopeId, claim.Event.WorkScopeId, StringComparison.Ordinal)
            || !string.Equals(scope.EquipmentId, claim.Event.EquipmentId, StringComparison.Ordinal))
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Quarantined",
                policy, decisionHash, decisionJson,
                "Projection.ScopeIdentityChanged",
                "The WorkScope no longer matches the immutable projection evidence.",
                TimeSpan.Zero, now, ct).ConfigureAwait(false);
        }

        if (scope.VersionNo != claim.WorkScope.VersionNo)
        {
            return await TransitionAsync(
                connection, transaction, claim, application!, "Retry",
                policy, decisionHash, decisionJson,
                "Projection.WorkScopeVersionChanged",
                $"WorkScope version changed from {claim.WorkScope.VersionNo} to {scope.VersionNo} after policy evaluation.",
                TimeSpan.FromSeconds(1), now, ct).ConfigureAwait(false);
        }
        return await TransitionAsync(
            connection, transaction, claim, application!, targetStatus,
            policy, decisionHash, decisionJson, errorCode, errorMessage,
            retryAfter, now, ct).ConfigureAwait(false);
    }

    private async Task<WorkScopeProjectionCommitResult?> ValidateAuthorityAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionClaim claim,
        ApplicationRow? application,
        CurrentIdentityRow? current,
        DateTime now,
        CancellationToken ct)
    {
        if (application is null
            || !string.Equals(application.ApplicationStatus, "Processing", StringComparison.Ordinal)
            || !string.Equals(application.LeaseOwner, claim.LeaseOwner, StringComparison.Ordinal)
            || application.LeaseFence != claim.LeaseFence
            || application.AttemptCount != claim.AttemptCount
            || application.LeaseExpiresAt is null
            || AsUtc(application.LeaseExpiresAt.Value) <= now)
        {
            return new WorkScopeProjectionCommitResult(WorkScopeProjectionCommitKind.LeaseLost);
        }

        if (current is not null
            && string.Equals(current.EventId, claim.Event.EventId, StringComparison.Ordinal)
            && current.SourceRevision == claim.Event.SourceRevision
            && AsUtc(current.AcceptedAt) == claim.Event.AcceptedAt.UtcDateTime)
        {
            return null;
        }

        return await TransitionAsync(
            connection,
            transaction,
            claim,
            application,
            "Superseded",
            null,
            null,
            null,
            "Projection.NotCurrent",
            "A newer immutable projection event became current before this decision committed.",
            TimeSpan.Zero,
            now,
            ct).ConfigureAwait(false);
    }

    private async Task<WorkScopeProjectionCommitResult> TransitionAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionClaim claim,
        ApplicationRow application,
        string targetStatus,
        WorkScopeProjectionPolicyIdentity? policy,
        string? decisionHash,
        string? decisionJson,
        string? errorCode,
        string? errorMessage,
        TimeSpan retryAfter,
        DateTime now,
        CancellationToken ct)
    {
        var retry = string.Equals(targetStatus, "Retry", StringComparison.Ordinal);
        var terminal = targetStatus is "Applied" or "Observed" or "Superseded" or "Quarantined";
        var updated = await ExecuteAsync(connection, transaction, TransitionSql, new
        {
            claim.Event.SourceClientId,
            claim.Event.EventId,
            FromStatus = application.ApplicationStatus,
            TargetStatus = targetStatus,
            claim.LeaseOwner,
            claim.LeaseFence,
            claim.AttemptCount,
            NextAttemptAt = retry ? now.Add(WorkScopeProjectionProcessor.BoundedRetry(retryAfter)) : (DateTime?)null,
            PolicyId = policy?.PolicyId,
            PolicyRevision = policy?.Version,
            DecisionHash = decisionHash,
            DecisionJson = decisionJson,
            ErrorCode = Trim(errorCode, 100),
            ErrorMessage = Trim(errorMessage, 2_000),
            CompletedAt = terminal ? now : (DateTime?)null,
            Now = now,
        }, ct).ConfigureAwait(false);
        if (updated != 1)
            return new WorkScopeProjectionCommitResult(WorkScopeProjectionCommitKind.LeaseLost);

        await InsertAuditAsync(
            connection,
            transaction,
            claim.Event.SourceClientId,
            claim.Event.EventId,
            targetStatus,
            application.ApplicationStatus,
            targetStatus,
            claim.AttemptCount,
            claim.LeaseFence,
            policy?.PolicyId,
            policy?.Version,
            decisionHash,
            decisionJson,
            Trim(errorCode, 100),
            Trim(errorMessage, 2_000),
            now,
            ct).ConfigureAwait(false);

        return new WorkScopeProjectionCommitResult(targetStatus switch
        {
            "Applied" => WorkScopeProjectionCommitKind.Applied,
            "Observed" => WorkScopeProjectionCommitKind.Observed,
            "Retry" => WorkScopeProjectionCommitKind.RetryScheduled,
            "Quarantined" => WorkScopeProjectionCommitKind.Quarantined,
            "Superseded" => WorkScopeProjectionCommitKind.Superseded,
            _ => throw new InvalidOperationException($"Unsupported projection status '{targetStatus}'."),
        }, errorMessage);
    }

    private async Task<string?> ValidateParentAsync(
        DbConnection connection,
        DbTransaction transaction,
        PomWorkScope scope,
        IReadOnlyList<WorkScopeProjectionEffect> effects,
        CancellationToken ct)
    {
        if (scope.ParentScopeId is null
            || !effects.Any(static effect => effect.Action is WorkScopeAction.Start
                or WorkScopeAction.Report or WorkScopeAction.Complete))
            return null;

        var parent = await QueryFirstOrDefaultAsync<ParentRow>(
            connection,
            transaction,
            _isSqlServer ? ParentSqlSqlServer : ParentSql,
            new { WorkScopeId = scope.ParentScopeId },
            ct).ConfigureAwait(false);
        if (parent is null) return "The parent WorkScope does not exist.";
        if (parent.Status is "Completed" or "Cancelled")
            return "A child WorkScope cannot execute under a terminal parent.";
        if (string.Equals(parent.IsHold, "Y", StringComparison.OrdinalIgnoreCase))
            return "A child WorkScope cannot execute while its parent is held.";
        return null;
    }

    private static NexaOne.Common.Result Apply(
        PomWorkScope scope,
        WorkScopeProjectionEffect effect,
        DateTime occurredAt)
    {
        var carrierId = effect.CarrierId ?? scope.CarrierId;
        if (scope.ScopeType == PomWorkScopeType.Carrier
            && carrierId is not null
            && !string.Equals(carrierId, scope.TargetId, StringComparison.OrdinalIgnoreCase))
        {
            return NexaOne.Common.Result.Failure(NexaOne.Common.Error.Validation(
                nameof(effect.CarrierId), "Carrier ID must match the carrier WorkScope target."));
        }

        return effect.Action switch
        {
            WorkScopeAction.Release => scope.Release("PROJECTION"),
            WorkScopeAction.Start => scope.Start(occurredAt, "PROJECTION"),
            WorkScopeAction.Report => scope.Report(
                effect.GoodQty ?? -1m, effect.DefectQty ?? -1m, "PROJECTION"),
            WorkScopeAction.Hold => scope.Hold("PROJECTION"),
            WorkScopeAction.ReleaseHold => scope.ReleaseHold("PROJECTION"),
            WorkScopeAction.Complete => scope.Complete(
                effect.GoodQty ?? -1m, effect.DefectQty ?? -1m, occurredAt, "PROJECTION"),
            WorkScopeAction.Cancel => scope.Cancel("PROJECTION"),
            _ => NexaOne.Common.Result.Failure(NexaOne.Common.Error.Validation(
                nameof(effect.Action), "WorkScope projection action is invalid.")),
        };
    }

    private Task<CurrentIdentityRow?> ReadCurrentAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionClaim claim,
        CancellationToken ct) => QueryFirstOrDefaultAsync<CurrentIdentityRow>(
        connection,
        transaction,
        _isSqlServer ? CurrentIdentitySqlSqlServer : CurrentIdentitySql,
        new
        {
            claim.Event.SourceClientId,
            claim.Event.EquipmentId,
            claim.Event.SequenceRunId,
        },
        ct);

    private Task<ApplicationRow?> ReadApplicationAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionClaim claim,
        CancellationToken ct) => QueryFirstOrDefaultAsync<ApplicationRow>(
        connection,
        transaction,
        _isSqlServer ? ApplicationSqlSqlServer : ApplicationSql,
        new { claim.Event.SourceClientId, claim.Event.EventId },
        ct);

    private static async Task InsertAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceClientId,
        string eventId,
        string eventType,
        string? fromStatus,
        string toStatus,
        int attemptCount,
        long leaseFence,
        string? policyId,
        string? policyRevision,
        string? decisionHash,
        string? decisionJson,
        string? errorCode,
        string? errorMessage,
        DateTime occurredAt,
        CancellationToken ct)
    {
        var auditId = ProjectionIdentity.Audit(
            sourceClientId, eventId, eventType, leaseFence, attemptCount);
        var inserted = await ExecuteAsync(connection, transaction, InsertAuditSql, new
        {
            ApplicationEventId = auditId,
            SourceClientId = sourceClientId,
            EventId = eventId,
            EventType = eventType,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            AttemptCount = attemptCount,
            LeaseFence = leaseFence,
            PolicyId = policyId,
            PolicyRevision = policyRevision,
            DecisionHash = decisionHash,
            DecisionJson = decisionJson,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            OccurredAt = occurredAt,
        }, ct).ConfigureAwait(false);
        if (inserted != 1)
            throw new DBConcurrencyException("Projection application audit insert did not affect exactly one row.");
    }

    private async Task<DateTime> ReadDatabaseUtcAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var value = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            _isSqlServer ? "SELECT SYSUTCDATETIME();" : "SELECT CURRENT_TIMESTAMP;",
            transaction: transaction,
            cancellationToken: ct)).ConfigureAwait(false);
        return AsUtc(value);
    }

    private async Task<T> RetryBusyAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (DbException ex) when (
                !_isSqlServer && attempt < MaxSqliteBusyRetries && IsSqliteBusy(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct)
                    .ConfigureAwait(false);
            }
            catch (DbException ex) when (
                _isSqlServer && attempt < MaxSqlServerDeadlockRetries && IsSqlServerDeadlock(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsSqliteBusy(DbException exception) =>
        exception.Message.Contains("locked", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("busy", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlServerDeadlock(DbException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().GetProperty("Number")?.GetValue(current) is int number
                && number == 1205)
            {
                return true;
            }
            if (current.Message.Contains("deadlock victim", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string Required(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static Task<T?> QueryFirstOrDefaultAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken ct) => connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(
        sql, parameters, transaction, cancellationToken: ct));

    private static Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition(
        sql, parameters, transaction, cancellationToken: ct));

    private static Task<int> ExecuteProbeAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition(
        sql, transaction: transaction, cancellationToken: ct));

    private const string ReadinessSelectSql = """
        SELECT A.SOURCE_CLIENT_ID, A.EVENT_ID, A.WORK_SCOPE_ID, A.EQUIPMENT_ID,
               A.SEQUENCE_RUN_ID, A.SOURCE_REVISION, A.ACCEPTED_AT,
               A.APPLICATION_STATUS, A.ATTEMPT_COUNT, A.NEXT_ATTEMPT_AT,
               A.LEASE_OWNER, A.LEASE_FENCE, A.LEASE_EXPIRES_AT,
               A.POLICY_ID, A.POLICY_REVISION, A.DECISION_HASH, A.DECISION_JSON,
               A.LAST_ERROR_CODE, A.LAST_ERROR_MESSAGE, A.COMPLETED_AT,
               A.CREATED_BY, A.CREATED_AT, A.UPDATED_BY, A.UPDATED_AT,
               C.SOURCE_CLIENT_ID, C.EQUIPMENT_ID, C.SEQUENCE_RUN_ID,
               C.EVENT_ID, C.WORK_SCOPE_ID, C.SOURCE_REVISION, C.ACCEPTED_AT,
               E.SOURCE_CLIENT_ID, E.EVENT_ID, E.WORK_SCOPE_ID, E.EQUIPMENT_ID,
               E.SEQUENCE_RUN_ID, E.SOURCE_REVISION, E.ACCEPTED_AT,
               E.REQUEST_HASH, E.OPERATION_KEY, E.PAIR_RUN_ID, E.PROJECTION_STATUS,
               E.TERMINAL_CLEANUP_COMPLETED, E.RECIPE_ID, E.RECIPE_SNAPSHOT_HASH,
               E.PROGRAM_HASH, E.CARRIERS_JSON, E.OCCURRED_AT, E.RESULT_CODE,
               E.RESULT_METADATA_JSON,
               R.SOURCE_CLIENT_ID, R.EVENT_ID, R.CARRIER_ID, R.LANE,
               R.CLEANING_RUN_ID, R.ACCEPTED_AT,
               S.WORK_SCOPE_ID, S.PLANT_ID, S.SCOPE_TYPE, S.TARGET_ID, S.NAME,
               S.PARENT_SCOPE_ID, S.WORK_ORDER_ID, S.CARRIER_ID, S.EQUIPMENT_ID,
               S.PRODUCT_ID, S.PROCESS_ID, S.RECIPE_ID, S.RECIPE_VERSION,
               S.PLAN_QTY, S.START_QTY, S.COMPLETE_QTY, S.SCRAP_QTY, S.OWNER_ID,
               S.STATUS, S.IS_HOLD, S.STARTED_AT, S.COMPLETED_AT, S.DESCRIPTION,
               S.VERSION_NO, S.CREATED_BY, S.CREATED_AT, S.UPDATED_BY, S.UPDATED_AT,
               S.CREATE_IDEMPOTENCY_KEY, S.CREATE_REQUEST_HASH
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
         CROSS JOIN POM_WORK_SCOPE_PROJECTION_CURRENT C
         CROSS JOIN POM_WORK_SCOPE_PROJECTION_INBOX E
         CROSS JOIN POM_WORK_SCOPE_PROJECTION_CARRIER R
         CROSS JOIN POM_WORK_SCOPE S
         WHERE 1 = 0
        """;

    private const string ReadinessApplicationUpdateSql = """
        UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
           SET APPLICATION_STATUS = APPLICATION_STATUS,
               ATTEMPT_COUNT = ATTEMPT_COUNT,
               NEXT_ATTEMPT_AT = NEXT_ATTEMPT_AT,
               LEASE_OWNER = LEASE_OWNER,
               LEASE_FENCE = LEASE_FENCE,
               LEASE_EXPIRES_AT = LEASE_EXPIRES_AT,
               POLICY_ID = POLICY_ID,
               POLICY_REVISION = POLICY_REVISION,
               DECISION_HASH = DECISION_HASH,
               DECISION_JSON = DECISION_JSON,
               LAST_ERROR_CODE = LAST_ERROR_CODE,
               LAST_ERROR_MESSAGE = LAST_ERROR_MESSAGE,
               COMPLETED_AT = COMPLETED_AT,
               UPDATED_BY = UPDATED_BY,
               UPDATED_AT = UPDATED_AT
         WHERE 1 = 0
        """;

    private const string ReadinessScopeUpdateSql = """
        UPDATE POM_WORK_SCOPE
           SET STATUS = STATUS,
               IS_HOLD = IS_HOLD,
               START_QTY = START_QTY,
               COMPLETE_QTY = COMPLETE_QTY,
               SCRAP_QTY = SCRAP_QTY,
               STARTED_AT = STARTED_AT,
               COMPLETED_AT = COMPLETED_AT,
               UPDATED_BY = UPDATED_BY,
               UPDATED_AT = UPDATED_AT,
               VERSION_NO = VERSION_NO
         WHERE 1 = 0
        """;

    private const string ReadinessApplicationEventInsertSql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
        (APPLICATION_EVENT_ID, SOURCE_CLIENT_ID, EVENT_ID, EVENT_TYPE,
         FROM_STATUS, TO_STATUS, ATTEMPT_COUNT, LEASE_FENCE,
         POLICY_ID, POLICY_REVISION, DECISION_HASH, DECISION_JSON,
         ERROR_CODE, ERROR_MESSAGE, OCCURRED_AT, CREATED_BY, CREATED_AT)
        SELECT NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL, NULL
         WHERE 1 = 0
        """;

    private const string ReadinessExecutionInsertSql = """
        INSERT INTO POM_WORK_SCOPE_EXECUTION
        (EXECUTION_ID, WORK_SCOPE_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
         GOOD_QTY, DEFECT_QTY, USER_ID, EQUIPMENT_ID, CLIENT_CHANNEL, DEVICE_ID,
         OCCURRED_AT, REMARK, EXPECTED_VERSION, RESULT_VERSION, CARRIER_ID,
         RESULT_CODE, RESULT_METADATA_JSON, CREATED_BY, CREATED_AT)
        SELECT NULL, NULL, NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL
         WHERE 1 = 0
        """;

    private const string ReadinessUniqueBindingIndexSqlServer = """
        SELECT COUNT_BIG(*)
          FROM sys.indexes I
         WHERE I.object_id = OBJECT_ID(N'POM_WORK_SCOPE_PROJECTION_CURRENT')
           AND I.name = N'UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE'
           AND I.is_unique = 1
           AND I.is_disabled = 0
           AND I.is_hypothetical = 0
           AND I.ignore_dup_key = 0
           AND I.has_filter = 0
           AND 1 = (
               SELECT COUNT_BIG(*)
                 FROM sys.index_columns IC
                WHERE IC.object_id = I.object_id
                  AND IC.index_id = I.index_id
                  AND IC.key_ordinal > 0)
           AND 1 = (
               SELECT COUNT_BIG(*)
                 FROM sys.index_columns IC
                 JOIN sys.columns C
                   ON C.object_id = IC.object_id
                  AND C.column_id = IC.column_id
                WHERE IC.object_id = I.object_id
                  AND IC.index_id = I.index_id
                  AND IC.key_ordinal = 1
                  AND C.name = N'WORK_SCOPE_ID'
                  AND C.collation_name = N'Latin1_General_100_BIN2')
        """;

    private const string ReadinessUniqueBindingIndexSqlite = """
        SELECT COUNT(*)
         FROM pragma_index_list('POM_WORK_SCOPE_PROJECTION_CURRENT')
         WHERE name = 'UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE'
           AND "unique" = 1
           AND partial = 0
           AND 1 = (
               SELECT COUNT(*)
                 FROM pragma_index_xinfo('UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE')
                WHERE key = 1)
           AND 1 = (
               SELECT COUNT(*)
                 FROM pragma_index_xinfo('UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE')
                WHERE seqno = 0
                  AND name = 'WORK_SCOPE_ID'
                  AND coll = 'BINARY'
                  AND key = 1)
        """;

    private const string CandidateSqlSqlServer = """
        SELECT TOP (1)
               A.SOURCE_CLIENT_ID AS SourceClientId,
               A.EVENT_ID AS EventId,
               A.APPLICATION_STATUS AS ApplicationStatus
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A WITH (UPDLOCK, READPAST, ROWLOCK)
          JOIN POM_WORK_SCOPE_PROJECTION_CURRENT C
            ON C.SOURCE_CLIENT_ID = A.SOURCE_CLIENT_ID
           AND C.EQUIPMENT_ID = A.EQUIPMENT_ID
           AND C.SEQUENCE_RUN_ID = A.SEQUENCE_RUN_ID
           AND C.EVENT_ID = A.EVENT_ID
           AND C.SOURCE_REVISION = A.SOURCE_REVISION
           AND C.ACCEPTED_AT = A.ACCEPTED_AT
         WHERE ((A.APPLICATION_STATUS IN ('Pending', 'Retry')
                  AND (A.NEXT_ATTEMPT_AT IS NULL OR A.NEXT_ATTEMPT_AT <= @Now))
                OR (A.APPLICATION_STATUS = 'Processing' AND A.LEASE_EXPIRES_AT <= @Now))
           AND A.ATTEMPT_COUNT < 2147483647
           AND A.LEASE_FENCE < 9223372036854775807
         ORDER BY A.ACCEPTED_AT, A.SOURCE_CLIENT_ID, A.EQUIPMENT_ID,
                  A.SEQUENCE_RUN_ID, A.SOURCE_REVISION, A.EVENT_ID
        """;

    private const string CandidateSqlSqlite = """
        SELECT A.SOURCE_CLIENT_ID AS SourceClientId,
               A.EVENT_ID AS EventId,
               A.APPLICATION_STATUS AS ApplicationStatus
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
          JOIN POM_WORK_SCOPE_PROJECTION_CURRENT C
            ON C.SOURCE_CLIENT_ID = A.SOURCE_CLIENT_ID
           AND C.EQUIPMENT_ID = A.EQUIPMENT_ID
           AND C.SEQUENCE_RUN_ID = A.SEQUENCE_RUN_ID
           AND C.EVENT_ID = A.EVENT_ID
           AND C.SOURCE_REVISION = A.SOURCE_REVISION
           AND C.ACCEPTED_AT = A.ACCEPTED_AT
         WHERE ((A.APPLICATION_STATUS IN ('Pending', 'Retry')
                  AND (A.NEXT_ATTEMPT_AT IS NULL OR A.NEXT_ATTEMPT_AT <= @Now))
                OR (A.APPLICATION_STATUS = 'Processing' AND A.LEASE_EXPIRES_AT <= @Now))
           AND A.ATTEMPT_COUNT < 2147483647
           AND A.LEASE_FENCE < 9223372036854775807
         ORDER BY A.ACCEPTED_AT, A.SOURCE_CLIENT_ID, A.EQUIPMENT_ID,
                  A.SEQUENCE_RUN_ID, A.SOURCE_REVISION, A.EVENT_ID
         LIMIT 1
        """;

    private const string ClaimSql = """
        UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
           SET APPLICATION_STATUS = 'Processing',
               ATTEMPT_COUNT = ATTEMPT_COUNT + 1,
               NEXT_ATTEMPT_AT = NULL,
               LEASE_OWNER = @LeaseOwner,
               LEASE_FENCE = LEASE_FENCE + 1,
               LEASE_EXPIRES_AT = @LeaseExpiresAt,
               COMPLETED_AT = NULL,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EVENT_ID = @EventId
           AND APPLICATION_STATUS = @FromStatus
           AND ((APPLICATION_STATUS IN ('Pending', 'Retry')
                  AND (NEXT_ATTEMPT_AT IS NULL OR NEXT_ATTEMPT_AT <= @Now))
                OR (APPLICATION_STATUS = 'Processing' AND LEASE_EXPIRES_AT <= @Now))
           AND ATTEMPT_COUNT < 2147483647
           AND LEASE_FENCE < 9223372036854775807
        """;

    private const string ClaimedApplicationSql = """
        SELECT SOURCE_CLIENT_ID AS SourceClientId, EVENT_ID AS EventId,
               LEASE_OWNER AS LeaseOwner, LEASE_FENCE AS LeaseFence,
               ATTEMPT_COUNT AS AttemptCount, LEASE_EXPIRES_AT AS LeaseExpiresAt
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
           AND APPLICATION_STATUS = 'Processing' AND LEASE_OWNER = @LeaseOwner
        """;

    private const string ClaimSnapshotSql = """
        SELECT E.SOURCE_CLIENT_ID AS SourceClientId, E.EVENT_ID AS EventId,
               E.REQUEST_HASH AS RequestHash, E.WORK_SCOPE_ID AS WorkScopeId,
               E.EQUIPMENT_ID AS EquipmentId, E.OPERATION_KEY AS OperationKey,
               E.PAIR_RUN_ID AS PairRunId, E.SEQUENCE_RUN_ID AS SequenceRunId,
               E.PROJECTION_STATUS AS ProjectionStatus,
               E.TERMINAL_CLEANUP_COMPLETED AS TerminalCleanupCompleted,
               E.RECIPE_ID AS EventRecipeId, E.RECIPE_SNAPSHOT_HASH AS RecipeSnapshotHash,
               E.PROGRAM_HASH AS ProgramHash,
               E.OCCURRED_AT AS OccurredAt, E.ACCEPTED_AT AS AcceptedAt,
               E.SOURCE_REVISION AS SourceRevision, E.RESULT_CODE AS EventResultCode,
               E.RESULT_METADATA_JSON AS EventResultMetadataJson,
               S.PLANT_ID AS PlantId, S.SCOPE_TYPE AS ScopeType,
               S.TARGET_ID AS TargetId, S.NAME AS ScopeName,
               S.PARENT_SCOPE_ID AS ParentScopeId, S.EQUIPMENT_ID AS ScopeEquipmentId,
               S.PRODUCT_ID AS ProductId, S.PROCESS_ID AS ProcessId,
               S.RECIPE_ID AS ScopeRecipeId, S.RECIPE_VERSION AS RecipeVersion,
               S.PLAN_QTY AS PlanQty, S.START_QTY AS StartQty,
               S.COMPLETE_QTY AS CompleteQty, S.SCRAP_QTY AS ScrapQty,
               S.OWNER_ID AS OwnerId, S.STATUS AS ScopeStatus, S.IS_HOLD AS IsHold,
               S.STARTED_AT AS StartedAt, S.COMPLETED_AT AS ScopeCompletedAt,
               S.DESCRIPTION AS Description, S.VERSION_NO AS VersionNo,
               S.CREATED_AT AS ScopeCreatedAt, S.CREATED_BY AS ScopeCreatedBy,
               S.UPDATED_AT AS ScopeUpdatedAt, S.UPDATED_BY AS ScopeUpdatedBy,
               S.WORK_ORDER_ID AS WorkOrderId, S.CARRIER_ID AS ScopeCarrierId
          FROM POM_WORK_SCOPE_PROJECTION_INBOX E
          JOIN POM_WORK_SCOPE S ON S.WORK_SCOPE_ID = E.WORK_SCOPE_ID
         WHERE E.SOURCE_CLIENT_ID = @SourceClientId AND E.EVENT_ID = @EventId
        """;

    private const string CarrierEvidenceSql = """
        SELECT LANE AS Lane, CARRIER_ID AS CarrierId, CLEANING_RUN_ID AS CleaningRunId
          FROM POM_WORK_SCOPE_PROJECTION_CARRIER
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
         ORDER BY LANE, CARRIER_ID
        """;

    private const string ApplicationSql = """
        SELECT APPLICATION_STATUS AS ApplicationStatus,
               ATTEMPT_COUNT AS AttemptCount, LEASE_OWNER AS LeaseOwner,
               LEASE_FENCE AS LeaseFence, LEASE_EXPIRES_AT AS LeaseExpiresAt
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
        """;

    private const string ApplicationSqlSqlServer = """
        SELECT APPLICATION_STATUS AS ApplicationStatus,
               ATTEMPT_COUNT AS AttemptCount, LEASE_OWNER AS LeaseOwner,
               LEASE_FENCE AS LeaseFence, LEASE_EXPIRES_AT AS LeaseExpiresAt
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION WITH (UPDLOCK, HOLDLOCK)
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
        """;

    private const string CurrentIdentitySql = """
        SELECT EVENT_ID AS EventId, SOURCE_REVISION AS SourceRevision, ACCEPTED_AT AS AcceptedAt
          FROM POM_WORK_SCOPE_PROJECTION_CURRENT
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
        """;

    private const string CurrentIdentitySqlSqlServer = """
        SELECT EVENT_ID AS EventId, SOURCE_REVISION AS SourceRevision, ACCEPTED_AT AS AcceptedAt
          FROM POM_WORK_SCOPE_PROJECTION_CURRENT WITH (UPDLOCK, HOLDLOCK)
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
        """;

    private const string ScopeForUpdateSql = SelectScopeSql + " WHERE WORK_SCOPE_ID = @WorkScopeId";
    private const string ScopeForUpdateSqlSqlServer = SelectScopeSql
        + " WITH (UPDLOCK, HOLDLOCK) WHERE WORK_SCOPE_ID = @WorkScopeId";

    private const string SelectScopeSql = """
        SELECT WORK_SCOPE_ID AS WorkScopeId, PLANT_ID AS PlantId, SCOPE_TYPE AS ScopeType,
               TARGET_ID AS TargetId, NAME AS Name, PARENT_SCOPE_ID AS ParentScopeId,
               WORK_ORDER_ID AS WorkOrderId, CARRIER_ID AS CarrierId,
               EQUIPMENT_ID AS EquipmentId, PRODUCT_ID AS ProductId, PROCESS_ID AS ProcessId,
               RECIPE_ID AS RecipeId, RECIPE_VERSION AS RecipeVersion, PLAN_QTY AS PlanQty,
               START_QTY AS StartQty, COMPLETE_QTY AS CompleteQty, SCRAP_QTY AS ScrapQty,
               OWNER_ID AS OwnerId, STATUS AS Status, IS_HOLD AS IsHold,
               STARTED_AT AS StartedAt, COMPLETED_AT AS CompletedAt,
               DESCRIPTION AS Description, VERSION_NO AS VersionNo,
               CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt,
               UPDATED_BY AS UpdatedBy, UPDATED_AT AS UpdatedAt,
               CREATE_IDEMPOTENCY_KEY AS CreateIdempotencyKey,
               CREATE_REQUEST_HASH AS CreateRequestHash
          FROM POM_WORK_SCOPE
        """;

    private const string ParentSql = """
        SELECT STATUS AS Status, IS_HOLD AS IsHold
          FROM POM_WORK_SCOPE WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string ParentSqlSqlServer = """
        SELECT STATUS AS Status, IS_HOLD AS IsHold
          FROM POM_WORK_SCOPE WITH (UPDLOCK, HOLDLOCK) WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string UpdateScopeSql = """
        UPDATE POM_WORK_SCOPE
           SET STATUS = @Status, IS_HOLD = @IsHold,
               START_QTY = @StartQty, COMPLETE_QTY = @CompleteQty, SCRAP_QTY = @ScrapQty,
               STARTED_AT = @StartedAt, COMPLETED_AT = @CompletedAt,
               UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt,
               VERSION_NO = VERSION_NO + @EffectCount
         WHERE WORK_SCOPE_ID = @WorkScopeId AND VERSION_NO = @ExpectedVersion
        """;

    private const string InsertExecutionSql = """
        INSERT INTO POM_WORK_SCOPE_EXECUTION
        (EXECUTION_ID, WORK_SCOPE_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
         GOOD_QTY, DEFECT_QTY, USER_ID, EQUIPMENT_ID, CLIENT_CHANNEL, DEVICE_ID, OCCURRED_AT,
         REMARK, EXPECTED_VERSION, RESULT_VERSION, CARRIER_ID, RESULT_CODE,
         RESULT_METADATA_JSON, CREATED_BY, CREATED_AT)
        VALUES
        (@ExecutionId, @WorkScopeId, @IdempotencyKey, @Action, @FromStatus, @ToStatus,
         @GoodQty, @DefectQty, @UserId, @EquipmentId, @ClientChannel, @DeviceId, @OccurredAt,
         @Remark, @ExpectedVersion, @ResultVersion, @CarrierId, @ResultCode,
         @ResultMetadataJson, @UserId, @OccurredAt)
        """;

    private const string TransitionSql = """
        UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
           SET APPLICATION_STATUS = @TargetStatus,
               NEXT_ATTEMPT_AT = @NextAttemptAt,
               LEASE_OWNER = NULL,
               LEASE_EXPIRES_AT = NULL,
               POLICY_ID = @PolicyId,
               POLICY_REVISION = @PolicyRevision,
               DECISION_HASH = @DecisionHash,
               DECISION_JSON = @DecisionJson,
               LAST_ERROR_CODE = @ErrorCode,
               LAST_ERROR_MESSAGE = @ErrorMessage,
               COMPLETED_AT = @CompletedAt,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
           AND APPLICATION_STATUS = @FromStatus
           AND APPLICATION_STATUS = 'Processing'
           AND LEASE_OWNER = @LeaseOwner
           AND LEASE_FENCE = @LeaseFence
           AND ATTEMPT_COUNT = @AttemptCount
           AND LEASE_EXPIRES_AT > @Now
        """;

    private const string InsertAuditSql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
        (APPLICATION_EVENT_ID, SOURCE_CLIENT_ID, EVENT_ID, EVENT_TYPE,
         FROM_STATUS, TO_STATUS, ATTEMPT_COUNT, LEASE_FENCE,
         POLICY_ID, POLICY_REVISION, DECISION_HASH, DECISION_JSON,
         ERROR_CODE, ERROR_MESSAGE, OCCURRED_AT, CREATED_BY, CREATED_AT)
        VALUES
        (@ApplicationEventId, @SourceClientId, @EventId, @EventType,
         @FromStatus, @ToStatus, @AttemptCount, @LeaseFence,
         @PolicyId, @PolicyRevision, @DecisionHash, @DecisionJson,
         @ErrorCode, @ErrorMessage, @OccurredAt, 'SYSTEM', @OccurredAt)
        """;

    private sealed class CandidateRow
    {
        public string SourceClientId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string ApplicationStatus { get; set; } = string.Empty;
    }

    private sealed class ApplicationRow
    {
        public string ApplicationStatus { get; set; } = string.Empty;
        public int AttemptCount { get; set; }
        public string? LeaseOwner { get; set; }
        public long LeaseFence { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
    }

    private sealed class CurrentIdentityRow
    {
        public string EventId { get; set; } = string.Empty;
        public long SourceRevision { get; set; }
        public DateTime AcceptedAt { get; set; }
    }

    private sealed class ParentRow
    {
        public string Status { get; set; } = string.Empty;
        public string IsHold { get; set; } = "N";
    }

    private sealed class ClaimLeaseRow
    {
        public string SourceClientId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string LeaseOwner { get; set; } = string.Empty;
        public long LeaseFence { get; set; }
        public int AttemptCount { get; set; }
        public DateTime LeaseExpiresAt { get; set; }
    }

    private sealed class CarrierEvidenceRow
    {
        public string Lane { get; set; } = string.Empty;
        public string CarrierId { get; set; } = string.Empty;
        public string CleaningRunId { get; set; } = string.Empty;
    }

    private sealed class ClaimRow
    {
        public string SourceClientId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string WorkScopeId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string OperationKey { get; set; } = string.Empty;
        public string PairRunId { get; set; } = string.Empty;
        public string SequenceRunId { get; set; } = string.Empty;
        public string ProjectionStatus { get; set; } = string.Empty;
        public bool TerminalCleanupCompleted { get; set; }
        public string EventRecipeId { get; set; } = string.Empty;
        public string RecipeSnapshotHash { get; set; } = string.Empty;
        public string ProgramHash { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public DateTime AcceptedAt { get; set; }
        public long SourceRevision { get; set; }
        public string EventResultCode { get; set; } = string.Empty;
        public string? EventResultMetadataJson { get; set; }
        public string PlantId { get; set; } = string.Empty;
        public string ScopeType { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string ScopeName { get; set; } = string.Empty;
        public string? ParentScopeId { get; set; }
        public string? ScopeEquipmentId { get; set; }
        public string? ProductId { get; set; }
        public string? ProcessId { get; set; }
        public string? ScopeRecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public decimal? PlanQty { get; set; }
        public decimal StartQty { get; set; }
        public decimal CompleteQty { get; set; }
        public decimal ScrapQty { get; set; }
        public string? OwnerId { get; set; }
        public string ScopeStatus { get; set; } = string.Empty;
        public string IsHold { get; set; } = "N";
        public DateTime? StartedAt { get; set; }
        public DateTime? ScopeCompletedAt { get; set; }
        public string? Description { get; set; }
        public int VersionNo { get; set; }
        public DateTime ScopeCreatedAt { get; set; }
        public string ScopeCreatedBy { get; set; } = string.Empty;
        public DateTime? ScopeUpdatedAt { get; set; }
        public string? ScopeUpdatedBy { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ScopeCarrierId { get; set; }

        public WorkScopeProjectionClaim ToClaim(
            ClaimLeaseRow lease,
            IReadOnlyList<CarrierEvidenceRow> carrierRows)
        {
            var carriers = carrierRows
                .Select(static carrier => new WorkScopeProjectionCarrierDto(
                    carrier.Lane, carrier.CarrierId, carrier.CleaningRunId))
                .ToArray();
            var evidence = new WorkScopeProjectionEventDto(
                SourceClientId,
                EventId,
                RequestHash,
                WorkScopeId,
                EquipmentId,
                OperationKey,
                PairRunId,
                SequenceRunId,
                Enum.Parse<WorkScopeProjectionStatus>(ProjectionStatus, ignoreCase: false),
                TerminalCleanupCompleted,
                EventRecipeId,
                RecipeSnapshotHash,
                ProgramHash,
                carriers,
                new DateTimeOffset(AsUtc(OccurredAt)),
                new DateTimeOffset(AsUtc(AcceptedAt)),
                SourceRevision,
                EventResultCode,
                EventResultMetadataJson);
            var scope = new WorkScopeDto(
                WorkScopeId,
                PlantId,
                ScopeType,
                TargetId,
                ScopeName,
                ParentScopeId,
                ScopeEquipmentId,
                ProductId,
                ProcessId,
                ScopeRecipeId,
                RecipeVersion,
                PlanQty,
                StartQty,
                CompleteQty,
                ScrapQty,
                OwnerId,
                ScopeStatus,
                string.Equals(IsHold, "Y", StringComparison.OrdinalIgnoreCase),
                StartedAt,
                ScopeCompletedAt,
                Description,
                VersionNo,
                AsUtc(ScopeCreatedAt),
                ScopeCreatedBy,
                ScopeUpdatedAt is null ? null : AsUtc(ScopeUpdatedAt.Value),
                ScopeUpdatedBy,
                WorkOrderId,
                ScopeCarrierId);
            return new WorkScopeProjectionClaim(
                evidence,
                scope,
                lease.LeaseOwner,
                lease.LeaseFence,
                lease.AttemptCount,
                new DateTimeOffset(AsUtc(lease.LeaseExpiresAt)));
        }
    }

    private sealed class ScopeRow
    {
        public string WorkScopeId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string ScopeType { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ParentScopeId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? CarrierId { get; set; }
        public string? EquipmentId { get; set; }
        public string? ProductId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public decimal? PlanQty { get; set; }
        public decimal StartQty { get; set; }
        public decimal CompleteQty { get; set; }
        public decimal ScrapQty { get; set; }
        public string? OwnerId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string IsHold { get; set; } = "N";
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Description { get; set; }
        public int VersionNo { get; set; }
        public string CreatedBy { get; set; } = "SYSTEM";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? CreateIdempotencyKey { get; set; }
        public string? CreateRequestHash { get; set; }

        public PomWorkScope ToDomain() => PomWorkScope.Restore(
            WorkScopeId,
            PlantId,
            Enum.Parse<PomWorkScopeType>(ScopeType, true),
            TargetId,
            Name,
            ParentScopeId,
            WorkOrderId,
            CarrierId,
            EquipmentId,
            ProductId,
            ProcessId,
            RecipeId,
            RecipeVersion,
            PlanQty,
            StartQty,
            CompleteQty,
            ScrapQty,
            OwnerId,
            Enum.Parse<PomWorkScopeStatus>(Status, true),
            string.Equals(IsHold, "Y", StringComparison.OrdinalIgnoreCase),
            StartedAt,
            CompletedAt,
            Description,
            VersionNo,
            CreatedBy,
            CreatedAt,
            UpdatedBy,
            UpdatedAt,
            CreateIdempotencyKey,
            CreateRequestHash);
    }

    private sealed class ExecutionRow
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string WorkScopeId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string FromStatus { get; set; } = string.Empty;
        public string ToStatus { get; set; } = string.Empty;
        public decimal? GoodQty { get; set; }
        public decimal? DefectQty { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? EquipmentId { get; set; }
        public string ClientChannel { get; set; } = "MES";
        public string? DeviceId { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? Remark { get; set; }
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string? CarrierId { get; set; }
        public string? ResultCode { get; set; }
        public string? ResultMetadataJson { get; set; }
    }
}

internal static class ProjectionIdentity
{
    public static (string ExecutionId, string IdempotencyKey) Execution(
        string sourceClientId,
        string eventId,
        WorkScopeProjectionPolicyIdentity policy,
        int ordinal)
    {
        var digest = Digest(
            sourceClientId,
            eventId,
            policy.PolicyId,
            policy.Version,
            ordinal.ToString(CultureInfo.InvariantCulture));
        return ($"pxe_{digest}", $"projection:{digest}");
    }

    public static string Audit(
        string sourceClientId,
        string eventId,
        string eventType,
        long leaseFence,
        int attemptCount) => $"pae_{Digest(
            sourceClientId,
            eventId,
            eventType,
            leaseFence.ToString(CultureInfo.InvariantCulture),
            attemptCount.ToString(CultureInfo.InvariantCulture))}";

    private static string Digest(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
