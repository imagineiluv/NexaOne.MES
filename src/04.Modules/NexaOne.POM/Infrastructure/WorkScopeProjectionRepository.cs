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

        var acceptedAt = DateTime.UtcNow;
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

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

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
          FROM POM_WORK_SCOPE
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
}
