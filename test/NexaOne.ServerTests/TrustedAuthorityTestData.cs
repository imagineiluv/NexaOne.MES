using Microsoft.Data.Sqlite;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.ServerTests;

internal sealed record TrustedAuthorityTestSeedOptions(
    string? RecipeSnapshotHash = null,
    string? ProgramHash = null,
    string? BoundRecipeSnapshotSchema = null,
    string? BoundRecipeSnapshotHash = null);

internal static class TrustedAuthorityTestData
{
    public static async Task SeedSqliteAsync(
        string connectionString,
        WorkScopeProjectionAuthorityEvidence evidence,
        TrustedAuthorityTestSeedOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TrustedAuthorityTestSeedOptions();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", null, ct);
        try
        {
            await ExecuteAsync(connection, """
                INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
                    (EXECUTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                     PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION,
                     RECIPE_SNAPSHOT_JSON, PARAMETER_SNAPSHOT_JSON, CONDITION_SNAPSHOT_JSON,
                     APPLIED_BY, APPLIED_AT, SOURCE, TRACE_ID, CREATED_AT,
                     WORK_SCOPE_ID, CARRIER_ID)
                SELECT @RecipeExecutionId, @ExecutionKey, @RequestHash, 'PLANT-1', @EquipmentId,
                       NULL, NULL, @OperationKey, @RecipeId, @RecipeVersion,
                       '{}', '{}', NULL, 'trusted-test', @OccurredAt, 'TEST', NULL, @OccurredAt,
                       @WorkScopeId, NULL
                 WHERE NOT EXISTS (
                    SELECT 1 FROM RMS_RECIPE_EXECUTION_SNAPSHOT S
                     WHERE S.EXECUTION_ID COLLATE BINARY = @RecipeExecutionId COLLATE BINARY);
                """, new
            {
                evidence.RecipeExecutionId,
                ExecutionKey = $"trusted:{evidence.RecipeExecutionId}",
                RequestHash = new string('D', 64),
                evidence.EquipmentId,
                evidence.OperationKey,
                evidence.RecipeId,
                evidence.RecipeVersion,
                evidence.WorkScopeId,
                OccurredAt = "2026-08-31T00:00:00.0000000Z",
            }, ct);
            await ExecuteAsync(connection, """
                INSERT INTO RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
                    (EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID, EQUIPMENT_ID,
                     OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH,
                     CAPTURED_AT)
                SELECT @RecipeExecutionId, @WorkScopeId, @PairRunId, @SequenceRunId, @EquipmentId,
                       @OperationKey, @RecipeId, @RecipeVersion, @RecipeSnapshotSchema,
                       @RecipeSnapshotHash, @OccurredAt
                 WHERE NOT EXISTS (
                    SELECT 1 FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE R
                     WHERE R.EXECUTION_ID COLLATE BINARY = @RecipeExecutionId COLLATE BINARY
                        OR (R.WORK_SCOPE_ID COLLATE BINARY = @WorkScopeId COLLATE BINARY
                            AND R.PAIR_RUN_ID COLLATE BINARY = @PairRunId COLLATE BINARY
                            AND R.SEQUENCE_RUN_ID COLLATE BINARY = @SequenceRunId COLLATE BINARY));
                """, new
            {
                evidence.RecipeExecutionId,
                evidence.WorkScopeId,
                evidence.PairRunId,
                evidence.SequenceRunId,
                evidence.EquipmentId,
                evidence.OperationKey,
                evidence.RecipeId,
                evidence.RecipeVersion,
                evidence.RecipeSnapshotSchema,
                RecipeSnapshotHash = (options.RecipeSnapshotHash ?? evidence.RecipeSnapshotHash)
                    .ToUpperInvariant(),
                OccurredAt = "2026-08-31T00:00:00.0000000Z",
            }, ct);
            await ExecuteAsync(connection, """
                INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT
                    (ARTIFACT_ID, EQUIPMENT_ID, OPERATION_KEY, PRODUCT_PROFILE_ID, PLUGIN_ID,
                     PRODUCT_DEFINITION_VERSION, PROGRAM_VERSION, PROGRAM_SCHEMA, PROGRAM_HASH,
                     BOUND_RECIPE_SNAPSHOT_SCHEMA, BOUND_RECIPE_SNAPSHOT_HASH,
                     RELEASED_AT, RELEASED_BY)
                SELECT @ProgramArtifactId, @EquipmentId, @OperationKey, 'cleaner-test',
                       'plugin.cleaner.test', 'product-v1', @ProgramVersion, @ProgramSchema,
                       @ProgramHash, @BoundRecipeSnapshotSchema, @BoundRecipeSnapshotHash,
                       @OccurredAt, 'trusted-test'
                 WHERE NOT EXISTS (
                    SELECT 1 FROM SYS_RELEASED_PROGRAM_ARTIFACT A
                     WHERE A.ARTIFACT_ID COLLATE BINARY = @ProgramArtifactId COLLATE BINARY);
                """, new
            {
                evidence.ProgramArtifactId,
                evidence.EquipmentId,
                evidence.OperationKey,
                ProgramVersion = evidence.ProgramArtifactId.Length <= 100
                    ? evidence.ProgramArtifactId
                    : evidence.ProgramArtifactId[..100],
                evidence.ProgramSchema,
                ProgramHash = (options.ProgramHash ?? evidence.ProgramHash).ToUpperInvariant(),
                BoundRecipeSnapshotSchema = options.BoundRecipeSnapshotSchema
                    ?? evidence.RecipeSnapshotSchema,
                BoundRecipeSnapshotHash = (options.BoundRecipeSnapshotHash
                    ?? evidence.RecipeSnapshotHash).ToUpperInvariant(),
                OccurredAt = "2026-08-31T00:00:00.0000000Z",
            }, ct);
            await ExecuteAsync(connection, "COMMIT;", null, ct);
        }
        catch
        {
            await ExecuteAsync(connection, "ROLLBACK;", null, CancellationToken.None);
            throw;
        }
    }

    public static async Task RevokeSqliteAsync(
        string connectionString,
        string artifactId,
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", null, ct);
        try
        {
            await ExecuteAsync(connection, """
                INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
                    (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
                SELECT @RevocationId, @ArtifactId, @OccurredAt, 'trusted-test', 'test revocation'
                 WHERE EXISTS (
                    SELECT 1 FROM SYS_RELEASED_PROGRAM_ARTIFACT A
                     WHERE A.ARTIFACT_ID COLLATE BINARY = @ArtifactId COLLATE BINARY)
                   AND NOT EXISTS (
                    SELECT 1 FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
                     WHERE R.ARTIFACT_ID COLLATE BINARY = @ArtifactId COLLATE BINARY);
                """, new
            {
                RevocationId = $"revoke:{artifactId}"[..Math.Min(100, $"revoke:{artifactId}".Length)],
                ArtifactId = artifactId,
                OccurredAt = "2026-08-31T00:01:00.0000000Z",
            }, ct);
            await ExecuteAsync(connection, "COMMIT;", null, ct);
        }
        catch
        {
            await ExecuteAsync(connection, "ROLLBACK;", null, CancellationToken.None);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        object? parameters,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var property in parameters.GetType().GetProperties())
                command.Parameters.AddWithValue($"@{property.Name}", property.GetValue(parameters) ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}
