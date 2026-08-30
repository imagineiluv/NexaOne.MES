using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
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
            SelectAuthoritySql + " WHERE WORK_SCOPE_ID = @workScopeId",
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
                    (connection, transaction) => ProvisionCoreAsync(
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
        }
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

        if (!scope.IsPristine)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                WorkScopeProjectionAuthorityProvisionKind.ScopeNotPristine,
                null);
        }

        if (!string.Equals(scope.WorkScopeId, evidence.WorkScopeId, StringComparison.Ordinal)
            || !string.Equals(scope.EquipmentId, evidence.EquipmentId, StringComparison.Ordinal)
            || !string.Equals(scope.TargetId, evidence.PairRunId, StringComparison.Ordinal)
            || !string.Equals(scope.RecipeId, evidence.RecipeId, StringComparison.Ordinal)
            || scope.RecipeVersion != evidence.RecipeVersion)
        {
            return new WorkScopeProjectionAuthorityProvisionResult(
                WorkScopeProjectionAuthorityProvisionKind.ScopeIdentityMismatch,
                null);
        }

        var provisionedAt = await ReadDatabaseUtcAsync(connection, transaction, ct)
            .ConfigureAwait(false);
        var row = AuthorityRow.From(
            evidence,
            scope.VersionNo,
            idempotencyKey,
            requestHash,
            actorId,
            provisionedAt);
        var inserted = await ExecuteAsync(
            connection,
            transaction,
            InsertAuthoritySql,
            row,
            ct).ConfigureAwait(false);
        if (inserted != 1)
            throw new DBConcurrencyException("Projection authority insert did not affect exactly one row.");

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

    private const string SelectAuthorityForUpdateSql = SelectAuthoritySql
        + " WHERE WORK_SCOPE_ID = @WorkScopeId";

    private const string SelectAuthorityForUpdateSqlSqlServer = SelectAuthoritySql
        + " WITH (UPDLOCK, HOLDLOCK) WHERE WORK_SCOPE_ID = @WorkScopeId";

    private const string SelectAuthorityByIdempotencySql = SelectAuthoritySql
        + " WHERE PROVISION_IDEMPOTENCY_KEY = @idempotencyKey";

    private const string SelectAuthorityByIdempotencySqlSqlServer = SelectAuthoritySql
        + " WITH (UPDLOCK, HOLDLOCK) WHERE PROVISION_IDEMPOTENCY_KEY = @idempotencyKey";

    private const string SelectAuthorityByEvidenceSql = SelectAuthoritySql + """
         WHERE RECIPE_EXECUTION_ID = @RecipeExecutionId
            OR (SOURCE_CLIENT_ID = @SourceClientId
                AND EQUIPMENT_ID = @EquipmentId
                AND SEQUENCE_RUN_ID = @SequenceRunId)
        """;

    private const string SelectAuthorityByEvidenceSqlSqlServer = SelectAuthoritySql + """
         WITH (UPDLOCK, HOLDLOCK)
         WHERE RECIPE_EXECUTION_ID = @RecipeExecutionId
            OR (SOURCE_CLIENT_ID = @SourceClientId
                AND EQUIPMENT_ID = @EquipmentId
                AND SEQUENCE_RUN_ID = @SequenceRunId)
        """;

    private const string SelectScopeForAuthoritySql = """
        SELECT S.WORK_SCOPE_ID AS WorkScopeId, S.TARGET_ID AS TargetId,
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
        SELECT S.WORK_SCOPE_ID AS WorkScopeId, S.TARGET_ID AS TargetId,
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

    private sealed class ScopeAuthorityRow
    {
        public string WorkScopeId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
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
