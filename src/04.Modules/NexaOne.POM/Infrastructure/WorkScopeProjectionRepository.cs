using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// Equipment snapshots의 append-only inbox와 monotonic current cursor를 한 transaction으로
/// 유지합니다. POM_WORK_SCOPE 업무 aggregate는 읽어서 identity만 검증하고 변경하지 않습니다.
/// </summary>
internal sealed class WorkScopeProjectionRepository : IWorkScopeProjectionInbox
{
    private const int MaxSqliteBusyRetries = 6;
    private const int MaxSqlServerDeadlockRetries = 3;
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _isSqlServer;

    public WorkScopeProjectionRepository(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
    }

    public async Task<WorkScopeProjectionPersistResult> PersistAsync(
        WorkScopeProjectionEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _processor.ExecuteInTransactionAsync(
                    (connection, transaction) => PersistCoreAsync(connection, transaction, envelope, ct),
                    IsolationLevel.Serializable,
                    ct).ConfigureAwait(false);
            }
            catch (DbException ex) when (
                !_isSqlServer
                && attempt < MaxSqliteBusyRetries
                && IsSqliteBusy(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct)
                    .ConfigureAwait(false);
            }
            catch (DbException ex) when (
                _isSqlServer
                && attempt < MaxSqlServerDeadlockRetries
                && IsSqlServerDeadlock(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<WorkScopeProjectionPersistResult> PersistCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionEnvelope envelope,
        CancellationToken ct)
    {
        // EventId is the transport identity and therefore precedes every mutable domain lookup.
        // Once accepted, replay/conflict semantics stay stable even if a scope is later removed or
        // a conflicting retry points at an unknown scope. SQL Server locks this identity/gap first;
        // all new-event writers then acquire the sequence cursor in the same order below.
        var existingEvent = await QueryFirstOrDefaultAsync<InboxIdentityRow>(
            connection,
            transaction,
            _isSqlServer ? EventIdentitySqlSqlServer : EventIdentitySql,
            envelope,
            ct).ConfigureAwait(false);
        if (existingEvent is not null)
        {
            if (!string.Equals(existingEvent.RequestHash, envelope.RequestHash, StringComparison.Ordinal))
                return Failure(WorkScopeProjectionPersistKind.EventHashConflict, envelope);

            var replayCurrent = await ReadCurrentAsync(connection, transaction, envelope, ct)
                .ConfigureAwait(false);
            if (string.Equals(replayCurrent?.EventId, envelope.EventId, StringComparison.Ordinal))
            {
                await EnsureCurrentApplicationAsync(
                    connection, transaction, envelope, AsUtc(existingEvent.AcceptedAt), ct)
                    .ConfigureAwait(false);
            }
            return new WorkScopeProjectionPersistResult(
                WorkScopeProjectionPersistKind.Replayed,
                envelope.SourceClientId,
                envelope.EventId,
                envelope.WorkScopeId,
                replayCurrent?.EventId == envelope.EventId,
                replayCurrent?.SourceRevision ?? envelope.SourceRevision,
                AsUtc(existingEvent.AcceptedAt));
        }

        var scope = await QueryFirstOrDefaultAsync<ScopeIdentityRow>(
            connection,
            transaction,
            _isSqlServer ? ScopeIdentitySqlSqlServer : ScopeIdentitySql,
            new { envelope.WorkScopeId },
            ct).ConfigureAwait(false);
        if (scope is null)
            return Failure(WorkScopeProjectionPersistKind.ScopeNotFound, envelope);

        // One WorkScope is owned by exactly one live projection stream. Locking the aggregate
        // before this reverse cursor lookup serializes concurrent first bindings even when both
        // streams have no current row yet; the database unique index is the final invariant fence.
        var scopeBinding = await QueryFirstOrDefaultAsync<WorkScopeBindingRow>(
            connection,
            transaction,
            _isSqlServer ? WorkScopeBindingSqlSqlServer : WorkScopeBindingSql,
            new { envelope.WorkScopeId },
            ct).ConfigureAwait(false);
        if (scopeBinding is not null
            && (!string.Equals(scopeBinding.SourceClientId, envelope.SourceClientId, StringComparison.Ordinal)
                || !string.Equals(scopeBinding.EquipmentId, envelope.EquipmentId, StringComparison.Ordinal)
                || !string.Equals(scopeBinding.SequenceRunId, envelope.SequenceRunId, StringComparison.Ordinal)))
        {
            return Failure(WorkScopeProjectionPersistKind.WorkScopeBindingConflict, envelope);
        }

        if (!string.Equals(scope.EquipmentId, envelope.EquipmentId, StringComparison.Ordinal))
        {
            return Failure(WorkScopeProjectionPersistKind.ScopeEquipmentConflict, envelope);
        }

        var current = await ReadCurrentAsync(connection, transaction, envelope, ct)
            .ConfigureAwait(false);

        if (current is not null
            && (!string.Equals(current.WorkScopeId, envelope.WorkScopeId, StringComparison.Ordinal)
                || !string.Equals(current.OperationKey, envelope.OperationKey, StringComparison.Ordinal)
                || !string.Equals(current.PairRunId, envelope.PairRunId, StringComparison.Ordinal)
                || !string.Equals(current.RecipeId, envelope.RecipeId, StringComparison.Ordinal)
                || !string.Equals(current.RecipeSnapshotHash, envelope.RecipeSnapshotHash, StringComparison.Ordinal)
                || !string.Equals(current.ProgramHash, envelope.ProgramHash, StringComparison.Ordinal)
                || !string.Equals(current.CarriersJson, envelope.CarriersJson, StringComparison.Ordinal)))
        {
            return Failure(WorkScopeProjectionPersistKind.SequenceIdentityConflict, envelope);
        }

        var acceptedAt = await ReadDatabaseUtcAsync(connection, transaction, ct).ConfigureAwait(false);
        if (current is not null && acceptedAt <= AsUtc(current.AcceptedAt))
            acceptedAt = AsUtc(current.AcceptedAt).AddTicks(1);
        var parameters = new
        {
            envelope.SourceClientId,
            envelope.EventId,
            envelope.RequestHash,
            envelope.WorkScopeId,
            envelope.EquipmentId,
            envelope.OperationKey,
            envelope.PairRunId,
            envelope.SequenceRunId,
            envelope.SourceRevision,
            envelope.ProjectionStatus,
            envelope.TerminalCleanupCompleted,
            envelope.RecipeId,
            envelope.RecipeSnapshotHash,
            envelope.ProgramHash,
            envelope.CarriersJson,
            envelope.ResultCode,
            envelope.ResultMetadataJson,
            envelope.OccurredAt,
            envelope.PayloadJson,
            AcceptedAt = acceptedAt,
        };
        var inserted = await ExecuteAsync(
            connection,
            transaction,
            _isSqlServer ? InsertInboxSql : InsertInboxSqlSqlite,
            parameters,
            ct).ConfigureAwait(false);
        if (inserted == 0)
        {
            // Another SQLite connection may have won between the read and INSERT OR IGNORE.
            // Re-read both identities inside a fresh retryable transaction outcome.
            existingEvent = await QueryFirstOrDefaultAsync<InboxIdentityRow>(
                connection, transaction, EventIdentitySql, envelope, ct).ConfigureAwait(false);
            if (existingEvent is not null)
            {
                if (!string.Equals(existingEvent.RequestHash, envelope.RequestHash, StringComparison.Ordinal))
                    return Failure(WorkScopeProjectionPersistKind.EventHashConflict, envelope);
                var concurrentCurrent = await ReadCurrentAsync(connection, transaction, envelope, ct)
                    .ConfigureAwait(false);
                if (string.Equals(concurrentCurrent?.EventId, envelope.EventId, StringComparison.Ordinal))
                {
                    await EnsureCurrentApplicationAsync(
                        connection, transaction, envelope, AsUtc(existingEvent.AcceptedAt), ct)
                        .ConfigureAwait(false);
                }
                return new WorkScopeProjectionPersistResult(
                    WorkScopeProjectionPersistKind.Replayed,
                    envelope.SourceClientId,
                    envelope.EventId,
                    envelope.WorkScopeId,
                    concurrentCurrent?.EventId == envelope.EventId,
                    concurrentCurrent?.SourceRevision ?? envelope.SourceRevision,
                    AsUtc(existingEvent.AcceptedAt));
            }
            throw new DBConcurrencyException(
                "Projection insert was ignored without an existing EventId identity.");
        }

        if (envelope.Carriers is null || envelope.Carriers.Count != 2)
            throw new InvalidDataException("A projection inbox event requires exactly two normalized carriers.");
        foreach (var carrier in envelope.Carriers)
        {
            var carrierInserted = await ExecuteAsync(
                connection,
                transaction,
                InsertCarrierSql,
                new
                {
                    envelope.SourceClientId,
                    envelope.EventId,
                    carrier.CarrierId,
                    carrier.Lane,
                    carrier.CleaningRunId,
                    AcceptedAt = acceptedAt,
                },
                ct).ConfigureAwait(false);
            if (carrierInserted != 1)
                throw new DBConcurrencyException(
                    "Normalized projection carrier insert did not affect exactly one row.");
        }

        // Cleaner drains its durable outbox in sequence order. Within one source revision the
        // MES acceptance order is therefore authoritative; OCCURRED_AT remains evidence only so
        // a later RecoveryRequired event cannot be suppressed by an equal/older source clock.
        var isCurrent = current is null
            || envelope.SourceRevision > current.SourceRevision
            || (envelope.SourceRevision == current.SourceRevision
                && acceptedAt > AsUtc(current.AcceptedAt));
        if (current is null)
        {
            await ExecuteAsync(connection, transaction, InsertCurrentSql, parameters, ct)
                .ConfigureAwait(false);
        }
        else if (isCurrent)
        {
            await ExecuteAsync(connection, transaction, UpdateCurrentSql, parameters, ct)
                .ConfigureAwait(false);
        }

        if (isCurrent)
        {
            await SupersedePriorApplicationsAsync(
                connection, transaction, envelope, acceptedAt, ct).ConfigureAwait(false);
            await EnsureCurrentApplicationAsync(
                connection, transaction, envelope, acceptedAt, ct).ConfigureAwait(false);
        }

        return new WorkScopeProjectionPersistResult(
            WorkScopeProjectionPersistKind.Accepted,
            envelope.SourceClientId,
            envelope.EventId,
            envelope.WorkScopeId,
            isCurrent,
            isCurrent ? envelope.SourceRevision : current!.SourceRevision,
            acceptedAt);
    }

    private Task<CurrentRow?> ReadCurrentAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionEnvelope envelope,
        CancellationToken ct) => QueryFirstOrDefaultAsync<CurrentRow>(
            connection,
            transaction,
            _isSqlServer ? CurrentSqlSqlServer : CurrentSql,
            envelope,
            ct);

    private async Task EnsureCurrentApplicationAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionEnvelope envelope,
        DateTime acceptedAt,
        CancellationToken ct)
    {
        var now = await ReadDatabaseUtcAsync(connection, transaction, ct).ConfigureAwait(false);
        var parameters = new
        {
            envelope.SourceClientId,
            envelope.EventId,
            envelope.WorkScopeId,
            envelope.EquipmentId,
            envelope.SequenceRunId,
            envelope.SourceRevision,
            AcceptedAt = acceptedAt,
            Now = now,
        };
        var inserted = await ExecuteAsync(
            connection, transaction, InsertApplicationSql, parameters, ct).ConfigureAwait(false);
        if (inserted == 1)
        {
            await InsertApplicationAuditAsync(
                connection, transaction, envelope.SourceClientId, envelope.EventId,
                "Pending", null, "Pending", 0, 0, null, null, null, null,
                null, null, now, ct).ConfigureAwait(false);
            return;
        }

        var existing = await QueryFirstOrDefaultAsync<ApplicationWakeRow>(
            connection,
            transaction,
            _isSqlServer ? ApplicationWakeSqlSqlServer : ApplicationWakeSql,
            parameters,
            ct).ConfigureAwait(false);
        if (existing is null)
            throw new DBConcurrencyException("Current projection has no durable application row.");

        var wakeable = string.Equals(existing.ApplicationStatus, "Processing", StringComparison.Ordinal)
            && existing.LeaseExpiresAt is { } expiry
            && AsUtc(expiry) <= now;
        if (!wakeable) return;

        var woken = await ExecuteAsync(connection, transaction, WakeApplicationSql, new
        {
            envelope.SourceClientId,
            envelope.EventId,
            FromStatus = existing.ApplicationStatus,
            existing.LeaseFence,
            Now = now,
        }, ct).ConfigureAwait(false);
        if (woken != 1) return;

        await InsertApplicationAuditAsync(
            connection, transaction, envelope.SourceClientId, envelope.EventId,
            "Pending", existing.ApplicationStatus, "Pending", existing.AttemptCount,
            existing.LeaseFence + 1, existing.PolicyId, existing.PolicyRevision,
            existing.DecisionHash, existing.DecisionJson, null, null, now, ct)
            .ConfigureAwait(false);
    }

    private async Task SupersedePriorApplicationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionEnvelope envelope,
        DateTime now,
        CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<SupersedeRow>(new CommandDefinition(
            _isSqlServer ? SupersedeCandidatesSqlSqlServer : SupersedeCandidatesSql,
            envelope,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        foreach (var row in rows)
        {
            var updated = await ExecuteAsync(connection, transaction, SupersedeApplicationSql, new
            {
                row.SourceClientId,
                row.EventId,
                row.ApplicationStatus,
                row.LeaseFence,
                Now = now,
            }, ct).ConfigureAwait(false);
            if (updated != 1) continue;
            await InsertApplicationAuditAsync(
                connection, transaction, row.SourceClientId, row.EventId,
                "Superseded", row.ApplicationStatus, "Superseded", row.AttemptCount,
                row.LeaseFence + 1, row.PolicyId, row.PolicyRevision,
                row.DecisionHash, row.DecisionJson,
                "Projection.NotCurrent", "A newer projection event became current.",
                now, ct).ConfigureAwait(false);
        }
    }

    private static async Task InsertApplicationAuditAsync(
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
        var inserted = await ExecuteAsync(connection, transaction, InsertApplicationAuditSql, new
        {
            ApplicationEventId = ProjectionIdentity.Audit(
                sourceClientId, eventId, eventType, leaseFence, attemptCount),
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

    private static WorkScopeProjectionPersistResult Failure(
        WorkScopeProjectionPersistKind kind,
        WorkScopeProjectionEnvelope envelope) => new(
        kind,
        envelope.SourceClientId,
        envelope.EventId,
        envelope.WorkScopeId,
        false,
        envelope.SourceRevision,
        DateTime.MinValue);

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

    private const string ScopeIdentitySql = """
        SELECT EQUIPMENT_ID AS EquipmentId
          FROM POM_WORK_SCOPE
         WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string ScopeIdentitySqlSqlServer = """
        SELECT EQUIPMENT_ID AS EquipmentId
          FROM POM_WORK_SCOPE WITH (UPDLOCK, HOLDLOCK)
         WHERE WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2 = @WorkScopeId
        """;

    private const string EventIdentitySql = """
        SELECT EVENT_ID AS EventId, REQUEST_HASH AS RequestHash, ACCEPTED_AT AS AcceptedAt
          FROM POM_WORK_SCOPE_PROJECTION_INBOX
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
        """;

    private const string EventIdentitySqlSqlServer = """
        SELECT EVENT_ID AS EventId, REQUEST_HASH AS RequestHash, ACCEPTED_AT AS AcceptedAt
          FROM POM_WORK_SCOPE_PROJECTION_INBOX WITH (UPDLOCK, HOLDLOCK)
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
        """;

    private const string CurrentSql = """
        SELECT EVENT_ID AS EventId, WORK_SCOPE_ID AS WorkScopeId,
               OPERATION_KEY AS OperationKey, PAIR_RUN_ID AS PairRunId,
               RECIPE_ID AS RecipeId, RECIPE_SNAPSHOT_HASH AS RecipeSnapshotHash,
               PROGRAM_HASH AS ProgramHash, CARRIERS_JSON AS CarriersJson,
               SOURCE_REVISION AS SourceRevision, ACCEPTED_AT AS AcceptedAt
          FROM POM_WORK_SCOPE_PROJECTION_CURRENT
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
        """;

    private const string CurrentSqlSqlServer = """
        SELECT EVENT_ID AS EventId, WORK_SCOPE_ID AS WorkScopeId,
               OPERATION_KEY AS OperationKey, PAIR_RUN_ID AS PairRunId,
               RECIPE_ID AS RecipeId, RECIPE_SNAPSHOT_HASH AS RecipeSnapshotHash,
               PROGRAM_HASH AS ProgramHash, CARRIERS_JSON AS CarriersJson,
               SOURCE_REVISION AS SourceRevision, ACCEPTED_AT AS AcceptedAt
          FROM POM_WORK_SCOPE_PROJECTION_CURRENT WITH (UPDLOCK, HOLDLOCK)
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
        """;

    private const string InsertInboxSql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_INBOX
        (SOURCE_CLIENT_ID, EVENT_ID, REQUEST_HASH, WORK_SCOPE_ID, EQUIPMENT_ID,
         OPERATION_KEY, PAIR_RUN_ID, SEQUENCE_RUN_ID, SOURCE_REVISION, PROJECTION_STATUS,
         TERMINAL_CLEANUP_COMPLETED, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH,
         CARRIERS_JSON, RESULT_CODE, RESULT_METADATA_JSON, OCCURRED_AT, PAYLOAD_JSON,
         ACCEPTED_AT, CREATED_BY, CREATED_AT)
        VALUES
        (@SourceClientId, @EventId, @RequestHash, @WorkScopeId, @EquipmentId,
         @OperationKey, @PairRunId, @SequenceRunId, @SourceRevision, @ProjectionStatus,
         @TerminalCleanupCompleted, @RecipeId, @RecipeSnapshotHash, @ProgramHash,
         @CarriersJson, @ResultCode, @ResultMetadataJson, @OccurredAt, @PayloadJson,
         @AcceptedAt, 'SYSTEM', @AcceptedAt)
        """;

    private static readonly string InsertInboxSqlSqlite = "INSERT OR IGNORE" +
        InsertInboxSql["INSERT".Length..];

    private const string InsertCarrierSql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_CARRIER
        (SOURCE_CLIENT_ID, EVENT_ID, CARRIER_ID, LANE, CLEANING_RUN_ID, ACCEPTED_AT)
        VALUES
        (@SourceClientId, @EventId, @CarrierId, @Lane, @CleaningRunId, @AcceptedAt)
        """;

    private const string WorkScopeBindingSql = """
        SELECT SOURCE_CLIENT_ID AS SourceClientId, EQUIPMENT_ID AS EquipmentId,
               SEQUENCE_RUN_ID AS SequenceRunId
          FROM POM_WORK_SCOPE_PROJECTION_CURRENT
         WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string WorkScopeBindingSqlSqlServer = """
        SELECT SOURCE_CLIENT_ID AS SourceClientId, EQUIPMENT_ID AS EquipmentId,
               SEQUENCE_RUN_ID AS SequenceRunId
          FROM POM_WORK_SCOPE_PROJECTION_CURRENT WITH (UPDLOCK, HOLDLOCK)
         WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string InsertCurrentSql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_CURRENT
        (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, EVENT_ID, WORK_SCOPE_ID,
         OPERATION_KEY, PAIR_RUN_ID, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH, CARRIERS_JSON,
         SOURCE_REVISION, PROJECTION_STATUS, TERMINAL_CLEANUP_COMPLETED,
         OCCURRED_AT, ACCEPTED_AT, UPDATED_AT)
        VALUES
        (@SourceClientId, @EquipmentId, @SequenceRunId, @EventId, @WorkScopeId,
         @OperationKey, @PairRunId, @RecipeId, @RecipeSnapshotHash, @ProgramHash, @CarriersJson,
         @SourceRevision, @ProjectionStatus, @TerminalCleanupCompleted,
         @OccurredAt, @AcceptedAt, @AcceptedAt)
        """;

    private const string UpdateCurrentSql = """
        UPDATE POM_WORK_SCOPE_PROJECTION_CURRENT
           SET EVENT_ID = @EventId,
               SOURCE_REVISION = @SourceRevision,
               PROJECTION_STATUS = @ProjectionStatus,
               TERMINAL_CLEANUP_COMPLETED = @TerminalCleanupCompleted,
               OCCURRED_AT = @OccurredAt,
               ACCEPTED_AT = @AcceptedAt,
               UPDATED_AT = @AcceptedAt
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
           AND (SOURCE_REVISION < @SourceRevision
                OR (SOURCE_REVISION = @SourceRevision AND ACCEPTED_AT < @AcceptedAt))
        """;

    private const string InsertApplicationSql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION
        (SOURCE_CLIENT_ID, EVENT_ID, WORK_SCOPE_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
         SOURCE_REVISION, ACCEPTED_AT, APPLICATION_STATUS, ATTEMPT_COUNT,
         NEXT_ATTEMPT_AT, LEASE_OWNER, LEASE_FENCE, LEASE_EXPIRES_AT,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @SourceClientId, @EventId, @WorkScopeId, @EquipmentId, @SequenceRunId,
               @SourceRevision, @AcceptedAt, 'Pending', 0,
               NULL, NULL, 0, NULL,
               'SYSTEM', @Now, 'SYSTEM', @Now
         WHERE EXISTS (
             SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_CURRENT C
              WHERE C.SOURCE_CLIENT_ID = @SourceClientId
                AND C.EQUIPMENT_ID = @EquipmentId
                AND C.SEQUENCE_RUN_ID = @SequenceRunId
                AND C.EVENT_ID = @EventId
                AND C.SOURCE_REVISION = @SourceRevision)
           AND NOT EXISTS (
             SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
              WHERE A.SOURCE_CLIENT_ID = @SourceClientId AND A.EVENT_ID = @EventId)
        """;

    private const string ApplicationWakeSql = """
        SELECT APPLICATION_STATUS AS ApplicationStatus,
               ATTEMPT_COUNT AS AttemptCount, LEASE_FENCE AS LeaseFence,
               LEASE_EXPIRES_AT AS LeaseExpiresAt,
               POLICY_ID AS PolicyId, POLICY_REVISION AS PolicyRevision,
               DECISION_HASH AS DecisionHash, DECISION_JSON AS DecisionJson
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
        """;

    private const string ApplicationWakeSqlSqlServer = """
        SELECT APPLICATION_STATUS AS ApplicationStatus,
               ATTEMPT_COUNT AS AttemptCount, LEASE_FENCE AS LeaseFence,
               LEASE_EXPIRES_AT AS LeaseExpiresAt,
               POLICY_ID AS PolicyId, POLICY_REVISION AS PolicyRevision,
               DECISION_HASH AS DecisionHash, DECISION_JSON AS DecisionJson
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION WITH (UPDLOCK, HOLDLOCK)
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
        """;

    private const string WakeApplicationSql = """
        UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
           SET APPLICATION_STATUS = 'Pending',
               NEXT_ATTEMPT_AT = NULL,
               LEASE_OWNER = NULL,
               LEASE_FENCE = LEASE_FENCE + 1,
               LEASE_EXPIRES_AT = NULL,
               LAST_ERROR_CODE = NULL,
               LAST_ERROR_MESSAGE = NULL,
               COMPLETED_AT = NULL,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
           AND APPLICATION_STATUS = @FromStatus
           AND LEASE_FENCE = @LeaseFence
           AND APPLICATION_STATUS = 'Processing'
           AND LEASE_EXPIRES_AT <= @Now
        """;

    private const string SupersedeCandidatesSql = """
        SELECT SOURCE_CLIENT_ID AS SourceClientId, EVENT_ID AS EventId,
               APPLICATION_STATUS AS ApplicationStatus, ATTEMPT_COUNT AS AttemptCount,
               LEASE_FENCE AS LeaseFence, POLICY_ID AS PolicyId,
               POLICY_REVISION AS PolicyRevision, DECISION_HASH AS DecisionHash,
               DECISION_JSON AS DecisionJson
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
           AND EVENT_ID <> @EventId
           AND APPLICATION_STATUS IN ('Pending', 'Processing', 'Retry')
        """;

    private const string SupersedeCandidatesSqlSqlServer = """
        SELECT SOURCE_CLIENT_ID AS SourceClientId, EVENT_ID AS EventId,
               APPLICATION_STATUS AS ApplicationStatus, ATTEMPT_COUNT AS AttemptCount,
               LEASE_FENCE AS LeaseFence, POLICY_ID AS PolicyId,
               POLICY_REVISION AS PolicyRevision, DECISION_HASH AS DecisionHash,
               DECISION_JSON AS DecisionJson
          FROM POM_WORK_SCOPE_PROJECTION_APPLICATION WITH (UPDLOCK, HOLDLOCK)
         WHERE SOURCE_CLIENT_ID = @SourceClientId
           AND EQUIPMENT_ID = @EquipmentId
           AND SEQUENCE_RUN_ID = @SequenceRunId
           AND EVENT_ID <> @EventId
           AND APPLICATION_STATUS IN ('Pending', 'Processing', 'Retry')
        """;

    private const string SupersedeApplicationSql = """
        UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
           SET APPLICATION_STATUS = 'Superseded',
               NEXT_ATTEMPT_AT = NULL,
               LEASE_OWNER = NULL,
               LEASE_FENCE = LEASE_FENCE + 1,
               LEASE_EXPIRES_AT = NULL,
               LAST_ERROR_CODE = 'Projection.NotCurrent',
               LAST_ERROR_MESSAGE = 'A newer projection event became current.',
               COMPLETED_AT = @Now,
               UPDATED_BY = 'SYSTEM',
               UPDATED_AT = @Now
         WHERE SOURCE_CLIENT_ID = @SourceClientId AND EVENT_ID = @EventId
           AND APPLICATION_STATUS = @ApplicationStatus
           AND LEASE_FENCE = @LeaseFence
           AND APPLICATION_STATUS IN ('Pending', 'Processing', 'Retry')
        """;

    private const string InsertApplicationAuditSql = """
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

    private sealed class ScopeIdentityRow
    {
        public string? EquipmentId { get; set; }
    }

    private sealed class InboxIdentityRow
    {
        public string EventId { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public DateTime AcceptedAt { get; set; }
    }

    private sealed class CurrentRow
    {
        public string EventId { get; set; } = string.Empty;
        public string WorkScopeId { get; set; } = string.Empty;
        public string OperationKey { get; set; } = string.Empty;
        public string PairRunId { get; set; } = string.Empty;
        public string RecipeId { get; set; } = string.Empty;
        public string RecipeSnapshotHash { get; set; } = string.Empty;
        public string ProgramHash { get; set; } = string.Empty;
        public string CarriersJson { get; set; } = string.Empty;
        public long SourceRevision { get; set; }
        public DateTime AcceptedAt { get; set; }
    }

    private sealed class WorkScopeBindingRow
    {
        public string SourceClientId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string SequenceRunId { get; set; } = string.Empty;
    }

    private class ApplicationWakeRow
    {
        public string ApplicationStatus { get; set; } = string.Empty;
        public int AttemptCount { get; set; }
        public long LeaseFence { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public string? PolicyId { get; set; }
        public string? PolicyRevision { get; set; }
        public string? DecisionHash { get; set; }
        public string? DecisionJson { get; set; }
    }

    private sealed class SupersedeRow : ApplicationWakeRow
    {
        public string SourceClientId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
    }
}
