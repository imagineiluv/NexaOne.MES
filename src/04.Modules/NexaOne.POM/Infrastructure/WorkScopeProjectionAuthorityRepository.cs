using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// Serializes first authority binding on the WorkScope aggregate. The authority row is independent
/// from V157 current backfill, so legacy evidence never acquires write ownership implicitly.
/// </summary>
internal sealed class WorkScopeProjectionAuthorityRepository
    : QueryRepository, IWorkScopeProjectionAuthorityRepository
{
    private const int MaxSqliteBusyRetries = 6;
    private const int MaxSqlServerDeadlockRetries = 3;
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _isSqlServer;

    public WorkScopeProjectionAuthorityRepository(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
    }

    public async Task<WorkScopeProjectionAuthorityRecord?> GetByWorkScopeIdAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<AuthorityRow>(
            _isSqlServer ? SelectAuthorityByWorkScopeIdSqlSqlServer : SelectAuthorityByWorkScopeIdSql,
            new { workScopeId },
            ct).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<WorkScopeProjectionAuthorityProvisionResult> ProvisionAsync(
        WorkScopeProjectionAuthorityEvidence evidence,
        string idempotencyKey,
        string requestHash,
        string actorId,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _processor.ExecuteInTransactionAsync(
                    (connection, transaction) => _isSqlServer
                        ? ProvisionSqlServerCoreAsync(
                            connection,
                            transaction,
                            evidence,
                            idempotencyKey,
                            requestHash,
                            actorId,
                            ct)
                        : ProvisionCoreAsync(
                            connection,
                            transaction,
                            evidence,
                            idempotencyKey,
                            requestHash,
                            actorId,
                            ct),
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
                && TryMapSqlServerAuthorityFailure(ex) is not null)
            {
                return new WorkScopeProjectionAuthorityProvisionResult(
                    TryMapSqlServerAuthorityFailure(ex)!.Value,
                    null);
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

    /// <summary>
    /// SQL Server deliberately delegates the complete lock graph and trust decision to one static
    /// database procedure. Keeping the repository out of the pre-read/lock path prevents a second,
    /// subtly different scope/authority/RMS/SYS ordering from deadlocking direct procedure callers.
    /// SQLite has no database-principal boundary and continues through <see cref="ProvisionCoreAsync"/>.
    /// </summary>
    private static async Task<WorkScopeProjectionAuthorityProvisionResult> ProvisionSqlServerCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionAuthorityEvidence evidence,
        string idempotencyKey,
        string requestHash,
        string actorId,
        CancellationToken ct)
    {
        var outcome = await QueryFirstOrDefaultInTransactionAsync<AuthorityInsertOutcome>(
            connection,
            transaction,
            InsertAuthoritySqlSqlServer,
            new
            {
                evidence.WorkScopeId,
                evidence.SourceClientId,
                evidence.EquipmentId,
                evidence.OperationKey,
                evidence.PairRunId,
                evidence.SequenceRunId,
                evidence.RecipeExecutionId,
                evidence.RecipeId,
                evidence.RecipeVersion,
                evidence.RecipeSnapshotSchema,
                evidence.RecipeSnapshotHash,
                evidence.ProgramArtifactId,
                evidence.ProgramSchema,
                evidence.ProgramHash,
                ProvisionIdempotencyKey = idempotencyKey,
                ProvisionRequestHash = requestHash,
                ProvisionedBy = actorId,
            },
            ct).ConfigureAwait(false)
            ?? throw new DBConcurrencyException(
                "Projection authority procedure returned no insert outcome.");

        var persisted = await QueryFirstOrDefaultInTransactionAsync<AuthorityRow>(
            connection,
            transaction,
            SelectAuthorityForUpdateSqlSqlServer,
            new { evidence.WorkScopeId },
            ct).ConfigureAwait(false)
            ?? throw new DBConcurrencyException(
                "Projection authority procedure returned without a persisted authority row.");

        return new WorkScopeProjectionAuthorityProvisionResult(
            outcome.Inserted == 1
                ? WorkScopeProjectionAuthorityProvisionKind.Provisioned
                : WorkScopeProjectionAuthorityProvisionKind.Replayed,
            persisted.ToRecord());
    }

    private async Task<WorkScopeProjectionAuthorityProvisionResult> ProvisionCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionAuthorityEvidence evidence,
        string idempotencyKey,
        string requestHash,
        string actorId,
        CancellationToken ct)
    {
        // SQL Server obtains the aggregate lock with UPDLOCK/HOLDLOCK. SQLite transactions start
        // deferred, so a harmless write forces writer ownership before the authority read.
        if (!_isSqlServer)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE POM_WORK_SCOPE SET UPDATED_AT = UPDATED_AT WHERE WORK_SCOPE_ID = @WorkScopeId",
                new { evidence.WorkScopeId },
                ct).ConfigureAwait(false);
        }

        var scope = await QueryFirstOrDefaultInTransactionAsync<ScopeAuthorityRow>(
            connection,
            transaction,
            _isSqlServer ? SelectScopeForAuthoritySqlSqlServer : SelectScopeForAuthoritySql,
            new { evidence.WorkScopeId },
            ct).ConfigureAwait(false);
        if (scope is null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                WorkScopeProjectionAuthorityProvisionKind.ScopeNotFound,
                null);
        }

        var existing = await QueryFirstOrDefaultInTransactionAsync<AuthorityRow>(
            connection,
            transaction,
            _isSqlServer ? SelectAuthorityForUpdateSqlSqlServer : SelectAuthorityForUpdateSql,
            new { evidence.WorkScopeId },
            ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var replay = string.Equals(existing.ProvisionIdempotencyKey, idempotencyKey, StringComparison.Ordinal)
                && string.Equals(existing.ProvisionRequestHash, requestHash, StringComparison.Ordinal)
                && existing.Matches(evidence);
            var sameIdempotencyKey = string.Equals(
                existing.ProvisionIdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal);
            return new WorkScopeProjectionAuthorityProvisionResult(
                replay
                    ? WorkScopeProjectionAuthorityProvisionKind.Replayed
                    : sameIdempotencyKey
                        ? WorkScopeProjectionAuthorityProvisionKind.IdempotencyConflict
                        : WorkScopeProjectionAuthorityProvisionKind.EvidenceConflict,
                replay ? existing.ToRecord() : null);
        }

        var idempotencyOwner = await QueryFirstOrDefaultInTransactionAsync<AuthorityRow>(
            connection,
            transaction,
            _isSqlServer ? SelectAuthorityByIdempotencySqlSqlServer : SelectAuthorityByIdempotencySql,
            new { idempotencyKey },
            ct).ConfigureAwait(false);
        if (idempotencyOwner is not null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                WorkScopeProjectionAuthorityProvisionKind.IdempotencyConflict,
                null);
        }

        var evidenceOwner = await QueryFirstOrDefaultInTransactionAsync<AuthorityRow>(
            connection,
            transaction,
            _isSqlServer ? SelectAuthorityByEvidenceSqlSqlServer : SelectAuthorityByEvidenceSql,
            evidence,
            ct).ConfigureAwait(false);
        if (evidenceOwner is not null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                WorkScopeProjectionAuthorityProvisionKind.EvidenceConflict,
                null);
        }

        var scopeFailure = ValidateScope(scope, evidence);
        if (scopeFailure is not null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                scopeFailure.Value,
                null);
        }

        var provisionedAt = await ReadDatabaseUtcAsync(connection, transaction, ct)
            .ConfigureAwait(false);

        // Keep a final fail-closed read immediately before the insert. The serializable aggregate
        // lock should make it identical to the first read, while this recheck also protects future
        // query/lock refactors from persisting authority after the scope identity changed.
        var finalScope = await QueryFirstOrDefaultInTransactionAsync<ScopeAuthorityRow>(
            connection,
            transaction,
            _isSqlServer ? SelectScopeForAuthoritySqlSqlServer : SelectScopeForAuthoritySql,
            new { evidence.WorkScopeId },
            ct).ConfigureAwait(false);
        if (finalScope is null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                WorkScopeProjectionAuthorityProvisionKind.ScopeNotFound,
                null);
        }

        scopeFailure = ValidateScope(finalScope, evidence);
        if (scopeFailure is not null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                scopeFailure.Value,
                null);
        }

        var trustedEvidenceFailure = await ValidateTrustedEvidenceAsync(
            connection,
            transaction,
            evidence,
            ct).ConfigureAwait(false);
        if (trustedEvidenceFailure is not null)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                trustedEvidenceFailure.Value,
                null);
        }

        var row = AuthorityRow.From(
            evidence,
            finalScope.VersionNo,
            idempotencyKey,
            requestHash,
            actorId,
            provisionedAt);
        if (_isSqlServer)
        {
            var outcome = await QueryFirstOrDefaultInTransactionAsync<AuthorityInsertOutcome>(
                connection,
                transaction,
                InsertAuthoritySqlSqlServer,
                row,
                ct).ConfigureAwait(false)
                ?? throw new DBConcurrencyException(
                    "Projection authority procedure returned no insert outcome.");
            row.ProvisionedAt = DateTime.SpecifyKind(outcome.RecordedAt, DateTimeKind.Utc);
            if (outcome.Inserted != 1)
            {
                var replayed = await QueryFirstOrDefaultInTransactionAsync<AuthorityRow>(
                    connection,
                    transaction,
                    SelectAuthorityForUpdateSqlSqlServer,
                    new { evidence.WorkScopeId },
                    ct).ConfigureAwait(false);
                var exactReplay = replayed is not null
                    && replayed.Matches(evidence)
                    && string.Equals(
                        replayed.ProvisionIdempotencyKey,
                        idempotencyKey,
                        StringComparison.Ordinal)
                    && string.Equals(
                        replayed.ProvisionRequestHash,
                        requestHash,
                        StringComparison.Ordinal);
                return new WorkScopeProjectionAuthorityProvisionResult(
                    exactReplay
                        ? WorkScopeProjectionAuthorityProvisionKind.Replayed
                        : WorkScopeProjectionAuthorityProvisionKind.EvidenceConflict,
                    exactReplay ? replayed!.ToRecord() : null);
            }
        }
        else
        {
            var inserted = await ExecuteAsync(
                connection,
                transaction,
                InsertAuthoritySql,
                row,
                ct).ConfigureAwait(false);
            if (inserted != 1)
                throw new DBConcurrencyException(
                    "Projection authority insert did not affect exactly one row.");
        }

        return new WorkScopeProjectionAuthorityProvisionResult(
            WorkScopeProjectionAuthorityProvisionKind.Provisioned,
            row.ToRecord());
    }

    private const string SelectAuthoritySql = """
        SELECT WORK_SCOPE_ID AS WorkScopeId, SOURCE_CLIENT_ID AS SourceClientId,
               EQUIPMENT_ID AS EquipmentId, OPERATION_KEY AS OperationKey,
               PAIR_RUN_ID AS PairRunId, SEQUENCE_RUN_ID AS SequenceRunId,
               RECIPE_EXECUTION_ID AS RecipeExecutionId, RECIPE_ID AS RecipeId,
               RECIPE_VERSION AS RecipeVersion, RECIPE_SNAPSHOT_SCHEMA AS RecipeSnapshotSchema,
               RECIPE_SNAPSHOT_HASH AS RecipeSnapshotHash,
               PROGRAM_ARTIFACT_ID AS ProgramArtifactId, PROGRAM_SCHEMA AS ProgramSchema,
               PROGRAM_HASH AS ProgramHash, BASELINE_VERSION_NO AS BaselineVersionNo,
               LAST_APPLIED_VERSION_NO AS LastAppliedVersionNo,
               PROVISION_IDEMPOTENCY_KEY AS ProvisionIdempotencyKey,
               PROVISION_REQUEST_HASH AS ProvisionRequestHash,
               PROVISIONED_AT AS ProvisionedAt, PROVISIONED_BY AS ProvisionedBy
          FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY
        """;

    private const string SelectAuthoritySqlSqlServer = """
        SELECT WORK_SCOPE_ID AS WorkScopeId, SOURCE_CLIENT_ID AS SourceClientId,
               EQUIPMENT_ID AS EquipmentId, OPERATION_KEY AS OperationKey,
               PAIR_RUN_ID AS PairRunId, SEQUENCE_RUN_ID AS SequenceRunId,
               RECIPE_EXECUTION_ID AS RecipeExecutionId, RECIPE_ID AS RecipeId,
               RECIPE_VERSION AS RecipeVersion, RECIPE_SNAPSHOT_SCHEMA AS RecipeSnapshotSchema,
               RECIPE_SNAPSHOT_HASH AS RecipeSnapshotHash,
               PROGRAM_ARTIFACT_ID AS ProgramArtifactId, PROGRAM_SCHEMA AS ProgramSchema,
               PROGRAM_HASH AS ProgramHash, BASELINE_VERSION_NO AS BaselineVersionNo,
               LAST_APPLIED_VERSION_NO AS LastAppliedVersionNo,
               PROVISION_IDEMPOTENCY_KEY AS ProvisionIdempotencyKey,
               PROVISION_REQUEST_HASH AS ProvisionRequestHash,
               PROVISIONED_AT AS ProvisionedAt, PROVISIONED_BY AS ProvisionedBy
          FROM POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY
        """;

    private const string SelectAuthorityForUpdateSql = SelectAuthoritySql
        + " WHERE WORK_SCOPE_ID = @WorkScopeId";

    private const string SelectAuthorityByWorkScopeIdSql = SelectAuthoritySql + """
         WHERE WORK_SCOPE_ID COLLATE BINARY = @workScopeId COLLATE BINARY
        """;

    private const string SelectAuthorityByWorkScopeIdSqlSqlServer = SelectAuthoritySqlSqlServer + """
         WHERE WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                 = @workScopeId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), WORK_SCOPE_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @workScopeId))
        """;

    private const string SelectAuthorityForUpdateSqlSqlServer = SelectAuthoritySqlSqlServer
        + " WITH (UPDLOCK, HOLDLOCK) WHERE WORK_SCOPE_ID = @WorkScopeId";

    private const string SelectAuthorityByIdempotencySql = SelectAuthoritySql
        + " WHERE PROVISION_IDEMPOTENCY_KEY = @idempotencyKey";

    private const string SelectAuthorityByIdempotencySqlSqlServer = SelectAuthoritySqlSqlServer
        + " WITH (UPDLOCK, HOLDLOCK) WHERE PROVISION_IDEMPOTENCY_KEY = @idempotencyKey";

    private const string SelectAuthorityByEvidenceSql = SelectAuthoritySql + """
         WHERE RECIPE_EXECUTION_ID = @RecipeExecutionId
            OR (SOURCE_CLIENT_ID = @SourceClientId
                AND EQUIPMENT_ID = @EquipmentId
                AND SEQUENCE_RUN_ID = @SequenceRunId)
        """;

    private const string SelectAuthorityByEvidenceSqlSqlServer = SelectAuthoritySqlSqlServer + """
         WITH (UPDLOCK, HOLDLOCK)
         WHERE RECIPE_EXECUTION_ID = @RecipeExecutionId
            OR (SOURCE_CLIENT_ID = @SourceClientId
                AND EQUIPMENT_ID = @EquipmentId
                AND SEQUENCE_RUN_ID = @SequenceRunId)
        """;

    private const string SelectScopeForAuthoritySql = """
        SELECT S.WORK_SCOPE_ID AS WorkScopeId, S.SCOPE_TYPE AS ScopeType,
               S.TARGET_ID AS TargetId, S.PROCESS_ID AS ProcessId,
               S.EQUIPMENT_ID AS EquipmentId, S.RECIPE_ID AS RecipeId,
               S.RECIPE_VERSION AS RecipeVersion, S.STATUS AS Status,
               S.IS_HOLD AS IsHold, S.VERSION_NO AS VersionNo,
               S.START_QTY AS StartQty, S.COMPLETE_QTY AS CompleteQty,
               S.SCRAP_QTY AS ScrapQty,
               CASE WHEN EXISTS (
                   SELECT 1 FROM POM_WORK_SCOPE_EXECUTION E
                    WHERE E.WORK_SCOPE_ID = S.WORK_SCOPE_ID) THEN 1 ELSE 0 END AS HasExecution
          FROM POM_WORK_SCOPE S
         WHERE S.WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string SelectScopeForAuthoritySqlSqlServer = """
        SELECT S.WORK_SCOPE_ID AS WorkScopeId, S.SCOPE_TYPE AS ScopeType,
               S.TARGET_ID AS TargetId, S.PROCESS_ID AS ProcessId,
               S.EQUIPMENT_ID AS EquipmentId, S.RECIPE_ID AS RecipeId,
               S.RECIPE_VERSION AS RecipeVersion, S.STATUS AS Status,
               S.IS_HOLD AS IsHold, S.VERSION_NO AS VersionNo,
               S.START_QTY AS StartQty, S.COMPLETE_QTY AS CompleteQty,
               S.SCRAP_QTY AS ScrapQty,
               CASE WHEN EXISTS (
                   SELECT 1 FROM POM_WORK_SCOPE_EXECUTION E WITH (HOLDLOCK)
                    WHERE E.WORK_SCOPE_ID = S.WORK_SCOPE_ID) THEN 1 ELSE 0 END AS HasExecution
          FROM POM_WORK_SCOPE S WITH (UPDLOCK, HOLDLOCK)
         WHERE S.WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string SelectExactRecipeEvidenceSql = """
        SELECT 1
          FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE R
         WHERE R.EXECUTION_ID COLLATE BINARY = @RecipeExecutionId COLLATE BINARY
           AND R.WORK_SCOPE_ID COLLATE BINARY = @WorkScopeId COLLATE BINARY
           AND R.PAIR_RUN_ID COLLATE BINARY = @PairRunId COLLATE BINARY
           AND R.SEQUENCE_RUN_ID COLLATE BINARY = @SequenceRunId COLLATE BINARY
           AND R.EQUIPMENT_ID COLLATE BINARY = @EquipmentId COLLATE BINARY
           AND R.OPERATION_KEY COLLATE BINARY = @OperationKey COLLATE BINARY
           AND R.RECIPE_ID COLLATE BINARY = @RecipeId COLLATE BINARY
           AND R.RECIPE_VERSION = @RecipeVersion
           AND R.SNAPSHOT_SCHEMA COLLATE BINARY = @RecipeSnapshotSchema COLLATE BINARY
           AND R.SNAPSHOT_HASH COLLATE BINARY = @RecipeSnapshotHash COLLATE BINARY
         LIMIT 1
        """;

    private const string SelectExactRecipeEvidenceSqlSqlServer = """
        SELECT TOP (1) 1
          FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE R WITH (UPDLOCK, HOLDLOCK)
         WHERE R.EXECUTION_ID COLLATE Latin1_General_100_BIN2
                 = @RecipeExecutionId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.EXECUTION_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeExecutionId))
           AND R.WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
                 = @WorkScopeId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.WORK_SCOPE_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @WorkScopeId))
           AND R.PAIR_RUN_ID COLLATE Latin1_General_100_BIN2
                 = @PairRunId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.PAIR_RUN_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @PairRunId))
           AND R.SEQUENCE_RUN_ID COLLATE Latin1_General_100_BIN2
                 = @SequenceRunId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.SEQUENCE_RUN_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @SequenceRunId))
           AND R.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2
                 = @EquipmentId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.EQUIPMENT_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           AND R.OPERATION_KEY COLLATE Latin1_General_100_BIN2
                 = @OperationKey COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.OPERATION_KEY))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           AND R.RECIPE_ID COLLATE Latin1_General_100_BIN2
                 = @RecipeId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.RECIPE_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeId))
           AND R.RECIPE_VERSION = @RecipeVersion
           AND R.SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2
                 = @RecipeSnapshotSchema COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.SNAPSHOT_SCHEMA))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSnapshotSchema))
           AND R.SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2
                 = @RecipeSnapshotHash COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.SNAPSHOT_HASH))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSnapshotHash))
        """;

    private const string SelectExactProgramArtifactSql = """
        SELECT 1
          FROM SYS_RELEASED_PROGRAM_ARTIFACT A
         WHERE A.ARTIFACT_ID COLLATE BINARY = @ProgramArtifactId COLLATE BINARY
           AND A.EQUIPMENT_ID COLLATE BINARY = @EquipmentId COLLATE BINARY
           AND A.OPERATION_KEY COLLATE BINARY = @OperationKey COLLATE BINARY
           AND A.PROGRAM_SCHEMA COLLATE BINARY = @ProgramSchema COLLATE BINARY
           AND A.PROGRAM_HASH COLLATE BINARY = @ProgramHash COLLATE BINARY
           AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE BINARY
                 = @RecipeSnapshotSchema COLLATE BINARY
           AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE BINARY
                 = @RecipeSnapshotHash COLLATE BINARY
         LIMIT 1
        """;

    private const string SelectExactProgramArtifactSqlSqlServer = """
        SELECT TOP (1) 1
          FROM SYS_RELEASED_PROGRAM_ARTIFACT A WITH (UPDLOCK, HOLDLOCK)
         WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2
                 = @ProgramArtifactId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramArtifactId))
           AND A.EQUIPMENT_ID COLLATE Latin1_General_100_BIN2
                 = @EquipmentId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.EQUIPMENT_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @EquipmentId))
           AND A.OPERATION_KEY COLLATE Latin1_General_100_BIN2
                 = @OperationKey COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.OPERATION_KEY))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @OperationKey))
           AND A.PROGRAM_SCHEMA COLLATE Latin1_General_100_BIN2
                 = @ProgramSchema COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_SCHEMA))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramSchema))
           AND A.PROGRAM_HASH COLLATE Latin1_General_100_BIN2
                 = @ProgramHash COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.PROGRAM_HASH))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramHash))
           AND A.BOUND_RECIPE_SNAPSHOT_SCHEMA COLLATE Latin1_General_100_BIN2
                 = @RecipeSnapshotSchema COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_SCHEMA))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSnapshotSchema))
           AND A.BOUND_RECIPE_SNAPSHOT_HASH COLLATE Latin1_General_100_BIN2
                 = @RecipeSnapshotHash COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.BOUND_RECIPE_SNAPSHOT_HASH))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @RecipeSnapshotHash))
        """;

    private const string SelectProgramRevocationSql = """
        SELECT 1
          FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
         WHERE R.ARTIFACT_ID COLLATE BINARY = @ProgramArtifactId COLLATE BINARY
         LIMIT 1
        """;

    private const string SelectProgramRevocationSqlSqlServer = """
        SELECT TOP (1) 1
          FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R WITH (UPDLOCK, HOLDLOCK)
         WHERE R.ARTIFACT_ID COLLATE Latin1_General_100_BIN2
                 = @ProgramArtifactId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.ARTIFACT_ID))
                 = DATALENGTH(CONVERT(NVARCHAR(MAX), @ProgramArtifactId))
        """;

    private const string InsertAuthoritySql = """
        INSERT INTO POM_WORK_SCOPE_PROJECTION_AUTHORITY
        (WORK_SCOPE_ID, SOURCE_CLIENT_ID, EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID,
         SEQUENCE_RUN_ID, RECIPE_EXECUTION_ID, RECIPE_ID, RECIPE_VERSION,
         RECIPE_SNAPSHOT_SCHEMA, RECIPE_SNAPSHOT_HASH, PROGRAM_ARTIFACT_ID,
         PROGRAM_SCHEMA, PROGRAM_HASH, BASELINE_VERSION_NO, LAST_APPLIED_VERSION_NO,
         PROVISION_IDEMPOTENCY_KEY, PROVISION_REQUEST_HASH, PROVISIONED_AT, PROVISIONED_BY,
         LAST_APPLIED_AT)
        VALUES
        (@WorkScopeId, @SourceClientId, @EquipmentId, @OperationKey, @PairRunId,
         @SequenceRunId, @RecipeExecutionId, @RecipeId, @RecipeVersion,
         @RecipeSnapshotSchema, @RecipeSnapshotHash, @ProgramArtifactId,
         @ProgramSchema, @ProgramHash, @BaselineVersionNo, @LastAppliedVersionNo,
         @ProvisionIdempotencyKey, @ProvisionRequestHash, @ProvisionedAt, @ProvisionedBy,
         NULL)
        """;

    private const string InsertAuthoritySqlSqlServer = """
        EXEC dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY
             @WorkScopeId = @WorkScopeId,
             @SourceClientId = @SourceClientId,
             @EquipmentId = @EquipmentId,
             @OperationKey = @OperationKey,
             @PairRunId = @PairRunId,
             @SequenceRunId = @SequenceRunId,
             @RecipeExecutionId = @RecipeExecutionId,
             @RecipeId = @RecipeId,
             @RecipeVersion = @RecipeVersion,
             @RecipeSnapshotSchema = @RecipeSnapshotSchema,
             @RecipeSnapshotHash = @RecipeSnapshotHash,
             @ProgramArtifactId = @ProgramArtifactId,
             @ProgramSchema = @ProgramSchema,
             @ProgramHash = @ProgramHash,
             @ProvisionIdempotencyKey = @ProvisionIdempotencyKey,
             @ProvisionRequestHash = @ProvisionRequestHash,
             @ProvisionedBy = @ProvisionedBy
        """;

    private static Task<T?> QueryFirstOrDefaultInTransactionAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken ct) => connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(
        sql,
        parameters,
        transaction,
        cancellationToken: ct));

    private static Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition(
        sql,
        parameters,
        transaction,
        cancellationToken: ct));

    private Task<DateTime> ReadDatabaseUtcAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct) => connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
        _isSqlServer ? "SELECT SYSUTCDATETIME();" : "SELECT STRFTIME('%Y-%m-%d %H:%M:%f', 'now');",
        transaction: transaction,
        cancellationToken: ct));

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

    private static WorkScopeProjectionAuthorityProvisionKind? TryMapSqlServerAuthorityFailure(
        DbException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().GetProperty("Number")?.GetValue(current) is not int number)
                continue;

            return number switch
            {
                51608 => WorkScopeProjectionAuthorityProvisionKind.ScopeNotFound,
                51609 => WorkScopeProjectionAuthorityProvisionKind.IdempotencyConflict,
                51610 => WorkScopeProjectionAuthorityProvisionKind.ScopeNotPristine,
                51611 => WorkScopeProjectionAuthorityProvisionKind.TrustedEvidenceMissing,
                51612 => WorkScopeProjectionAuthorityProvisionKind.TrustedEvidenceRevoked,
                51613 => WorkScopeProjectionAuthorityProvisionKind.RuntimeProductBindingMissing,
                51614 => WorkScopeProjectionAuthorityProvisionKind.ScopeIdentityMismatch,
                51615 => WorkScopeProjectionAuthorityProvisionKind.EvidenceConflict,
                _ => null,
            };
        }

        return null;
    }

    private async Task<WorkScopeProjectionAuthorityProvisionKind?> ValidateTrustedEvidenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkScopeProjectionAuthorityEvidence evidence,
        CancellationToken ct)
    {
        var exactRecipe = await QueryFirstOrDefaultInTransactionAsync<int?>(
            connection,
            transaction,
            _isSqlServer ? SelectExactRecipeEvidenceSqlSqlServer : SelectExactRecipeEvidenceSql,
            evidence,
            ct).ConfigureAwait(false);
        if (exactRecipe is null)
            return WorkScopeProjectionAuthorityProvisionKind.TrustedEvidenceMissing;

        // Lock the immutable artifact before its revocation key/range. Direct revocation writers
        // are not yet forced into that order by the database, so SQL Server 1205 is retried above
        // in a fresh serializable transaction and remains an MSSQL commissioning gate.
        var exactProgram = await QueryFirstOrDefaultInTransactionAsync<int?>(
            connection,
            transaction,
            _isSqlServer ? SelectExactProgramArtifactSqlSqlServer : SelectExactProgramArtifactSql,
            evidence,
            ct).ConfigureAwait(false);
        if (exactProgram is null)
            return WorkScopeProjectionAuthorityProvisionKind.TrustedEvidenceMissing;

        var revocation = await QueryFirstOrDefaultInTransactionAsync<int?>(
            connection,
            transaction,
            _isSqlServer ? SelectProgramRevocationSqlSqlServer : SelectProgramRevocationSql,
            evidence,
            ct).ConfigureAwait(false);
        return revocation is null
            ? null
            : WorkScopeProjectionAuthorityProvisionKind.TrustedEvidenceRevoked;
    }

    private static WorkScopeProjectionAuthorityProvisionKind? ValidateScope(
        ScopeAuthorityRow scope,
        WorkScopeProjectionAuthorityEvidence evidence)
    {
        if (!scope.IsPristine)
            return WorkScopeProjectionAuthorityProvisionKind.ScopeNotPristine;

        return string.Equals(scope.WorkScopeId, evidence.WorkScopeId, StringComparison.Ordinal)
            && string.Equals(scope.ScopeType, nameof(PomWorkScopeType.Other), StringComparison.Ordinal)
            && string.Equals(scope.EquipmentId, evidence.EquipmentId, StringComparison.Ordinal)
            && string.Equals(scope.ProcessId, evidence.OperationKey, StringComparison.Ordinal)
            && string.Equals(scope.TargetId, evidence.PairRunId, StringComparison.Ordinal)
            && string.Equals(scope.RecipeId, evidence.RecipeId, StringComparison.Ordinal)
            && scope.RecipeVersion == evidence.RecipeVersion
                ? null
                : WorkScopeProjectionAuthorityProvisionKind.ScopeIdentityMismatch;
    }

    private sealed class ScopeAuthorityRow
    {
        public string WorkScopeId { get; set; } = string.Empty;
        public string ScopeType { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string? ProcessId { get; set; }
        public string? EquipmentId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public string Status { get; set; } = string.Empty;
        public string IsHold { get; set; } = string.Empty;
        public int VersionNo { get; set; }
        public decimal StartQty { get; set; }
        public decimal CompleteQty { get; set; }
        public decimal ScrapQty { get; set; }
        public int HasExecution { get; set; }

        public bool IsPristine =>
            string.Equals(Status, "Created", StringComparison.Ordinal)
            && string.Equals(IsHold, "N", StringComparison.Ordinal)
            && VersionNo == 1
            && StartQty == 0
            && CompleteQty == 0
            && ScrapQty == 0
            && HasExecution == 0;
    }

    private sealed class AuthorityInsertOutcome
    {
        public int Inserted { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    private sealed class AuthorityRow
    {
        public string WorkScopeId { get; set; } = string.Empty;
        public string SourceClientId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string OperationKey { get; set; } = string.Empty;
        public string PairRunId { get; set; } = string.Empty;
        public string SequenceRunId { get; set; } = string.Empty;
        public string RecipeExecutionId { get; set; } = string.Empty;
        public string RecipeId { get; set; } = string.Empty;
        public int RecipeVersion { get; set; }
        public string RecipeSnapshotSchema { get; set; } = string.Empty;
        public string RecipeSnapshotHash { get; set; } = string.Empty;
        public string ProgramArtifactId { get; set; } = string.Empty;
        public string ProgramSchema { get; set; } = string.Empty;
        public string ProgramHash { get; set; } = string.Empty;
        public int BaselineVersionNo { get; set; }
        public int LastAppliedVersionNo { get; set; }
        public string ProvisionIdempotencyKey { get; set; } = string.Empty;
        public string ProvisionRequestHash { get; set; } = string.Empty;
        public DateTime ProvisionedAt { get; set; }
        public string ProvisionedBy { get; set; } = string.Empty;

        public bool Matches(WorkScopeProjectionAuthorityEvidence evidence) =>
            string.Equals(WorkScopeId, evidence.WorkScopeId, StringComparison.Ordinal)
            && string.Equals(SourceClientId, evidence.SourceClientId, StringComparison.Ordinal)
            && string.Equals(EquipmentId, evidence.EquipmentId, StringComparison.Ordinal)
            && string.Equals(OperationKey, evidence.OperationKey, StringComparison.Ordinal)
            && string.Equals(PairRunId, evidence.PairRunId, StringComparison.Ordinal)
            && string.Equals(SequenceRunId, evidence.SequenceRunId, StringComparison.Ordinal)
            && string.Equals(RecipeExecutionId, evidence.RecipeExecutionId, StringComparison.Ordinal)
            && string.Equals(RecipeId, evidence.RecipeId, StringComparison.Ordinal)
            && RecipeVersion == evidence.RecipeVersion
            && string.Equals(RecipeSnapshotSchema, evidence.RecipeSnapshotSchema, StringComparison.Ordinal)
            && string.Equals(RecipeSnapshotHash, evidence.RecipeSnapshotHash, StringComparison.Ordinal)
            && string.Equals(ProgramArtifactId, evidence.ProgramArtifactId, StringComparison.Ordinal)
            && string.Equals(ProgramSchema, evidence.ProgramSchema, StringComparison.Ordinal)
            && string.Equals(ProgramHash, evidence.ProgramHash, StringComparison.Ordinal);

        public WorkScopeProjectionAuthorityRecord ToRecord() => new(
            WorkScopeId,
            SourceClientId,
            EquipmentId,
            OperationKey,
            PairRunId,
            SequenceRunId,
            RecipeExecutionId,
            RecipeId,
            RecipeVersion,
            RecipeSnapshotSchema,
            RecipeSnapshotHash,
            ProgramArtifactId,
            ProgramSchema,
            ProgramHash,
            BaselineVersionNo,
            LastAppliedVersionNo,
            ProvisionIdempotencyKey,
            ProvisionRequestHash,
            DateTime.SpecifyKind(ProvisionedAt, DateTimeKind.Utc),
            ProvisionedBy);

        public static AuthorityRow From(
            WorkScopeProjectionAuthorityEvidence evidence,
            int baselineVersionNo,
            string idempotencyKey,
            string requestHash,
            string actorId,
            DateTime provisionedAt) => new()
        {
            WorkScopeId = evidence.WorkScopeId,
            SourceClientId = evidence.SourceClientId,
            EquipmentId = evidence.EquipmentId,
            OperationKey = evidence.OperationKey,
            PairRunId = evidence.PairRunId,
            SequenceRunId = evidence.SequenceRunId,
            RecipeExecutionId = evidence.RecipeExecutionId,
            RecipeId = evidence.RecipeId,
            RecipeVersion = evidence.RecipeVersion,
            RecipeSnapshotSchema = evidence.RecipeSnapshotSchema,
            RecipeSnapshotHash = evidence.RecipeSnapshotHash,
            ProgramArtifactId = evidence.ProgramArtifactId,
            ProgramSchema = evidence.ProgramSchema,
            ProgramHash = evidence.ProgramHash,
            BaselineVersionNo = baselineVersionNo,
            LastAppliedVersionNo = baselineVersionNo,
            ProvisionIdempotencyKey = idempotencyKey,
            ProvisionRequestHash = requestHash,
            ProvisionedAt = DateTime.SpecifyKind(provisionedAt, DateTimeKind.Utc),
            ProvisionedBy = actorId,
        };
    }
}
