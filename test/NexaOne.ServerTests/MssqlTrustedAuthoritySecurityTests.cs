using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
public sealed class MssqlTrustedAuthoritySecurityTests
{
    private readonly ITestOutputHelper _output;

    public MssqlTrustedAuthoritySecurityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task No_login_users_can_only_use_their_writer_boundary_and_runtime_binding()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var runtime1 = $"sec_runtime1_{suffix}";
        var runtime2 = $"sec_runtime2_{suffix}";
        var rmsWriter = $"sec_rms_{suffix}";
        var sysWriter = $"sec_sys_{suffix}";
        var unboundRuntime = $"sec_unbound_{suffix}";
        var workScopeId = $"SEC_WS_{suffix}";
        var equipmentId = $"SEC_EQ_{suffix}";
        var operationKey = $"SEC_OP_{suffix}";
        var pairRunId = $"SEC_PAIR_{suffix}";
        var sequenceRunId = $"SEC_SEQ_{suffix}";
        var executionId = $"SEC_EX_{suffix}";
        var artifactId = $"SEC_ART_{suffix}";
        var rollingArtifactId = $"SEC_ART_NEXT_{suffix}";
        var rogueProcedure = $"SEC_TRUSTED_BACKDOOR_{suffix}";
        var rogueHelperProcedure = $"SEC_TRUSTED_HELPER_{suffix}";
        var rogueSynonym = $"SEC_TRUSTED_SYNONYM_{suffix}";
        var rogueSchema = $"SEC_TRUSTED_SCHEMA_{suffix}";
        var rogueServerRole = $"sec_server_role_{suffix}";
        var impersonator = $"sec_impersonator_{suffix}";
        var recipeSchema = "security-recipe-v1";
        var programSchema = "security-program-v1";
        var recipeHash = new string('A', 64);
        var programHash = new string('B', 64);
        var rollingProgramHash = new string('E', 64);

        await database.ExecuteAsync($"""
            CREATE USER [{runtime1}] WITHOUT LOGIN;
            CREATE USER [{runtime2}] WITHOUT LOGIN;
            CREATE USER [{unboundRuntime}] WITHOUT LOGIN;
            CREATE USER [{rmsWriter}] WITHOUT LOGIN;
            CREATE USER [{sysWriter}] WITHOUT LOGIN;
            """);
        (await database.ScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT CONVERT(VARCHAR(170), sid, 2))
              FROM sys.database_principals
             WHERE name IN (@Runtime, @RmsWriter, @SysWriter);
            """,
            new { Runtime = runtime1, RmsWriter = rmsWriter, SysWriter = sysWriter })).Should().Be(3,
            "runtime and both trusted writers must have distinct database SIDs");
        try
        {
            var bootstrap = await RunCommissioningAsync(
                database.ConnectionString,
                new[]
                {
                    "-RuntimeDatabaseUser", runtime1,
                    "-RmsWriterDatabaseUser", rmsWriter,
                    "-SysWriterDatabaseUser", sysWriter,
                    "-Apply", "-WriterBootstrapOnly",
                });
            bootstrap.ExitCode.Should().Be(0, bootstrap.Output);
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneProjectionRuntime', @Runtime), 0);",
                new { Runtime = runtime1 })).Should().Be(0,
                "writer bootstrap must never enable runtime authority");
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneRmsEvidenceWriter', @Writer), 0);",
                new { Writer = rmsWriter })).Should().Be(1);
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', @Writer), 0);",
                new { Writer = sysWriter })).Should().Be(1);

            await database.ExecuteAsync(
                """
                INSERT INTO POM_WORK_SCOPE
                    (WORK_SCOPE_ID, PLANT_ID, SCOPE_TYPE, TARGET_ID, NAME, EQUIPMENT_ID,
                     PROCESS_ID, RECIPE_ID, RECIPE_VERSION, PLAN_QTY, CREATED_BY, UPDATED_BY,
                     CREATE_IDEMPOTENCY_KEY, CREATE_REQUEST_HASH)
                VALUES
                    (@WorkScopeId, N'SEC_PLANT', N'Other', @PairRunId, N'security contract',
                     @EquipmentId, @OperationKey, N'SEC_RECIPE', 1, 1, N'security-test',
                     N'security-test', @CreateKey, @RequestHash);

                INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
                    (EXECUTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
                     PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION,
                     RECIPE_SNAPSHOT_JSON, PARAMETER_SNAPSHOT_JSON, CONDITION_SNAPSHOT_JSON,
                     APPLIED_BY, APPLIED_AT, SOURCE, TRACE_ID, CREATED_AT,
                     WORK_SCOPE_ID, CARRIER_ID)
                VALUES
                    (@ExecutionId, @ExecutionKey, @RequestHash, N'SEC_PLANT', @EquipmentId,
                     NULL, NULL, @OperationKey, N'SEC_RECIPE', 1, N'{}', N'{}', NULL,
                     N'security-test', SYSUTCDATETIME(), N'TEST', NULL, SYSUTCDATETIME(),
                     @WorkScopeId, NULL);
                """,
                new
                {
                    WorkScopeId = workScopeId,
                    PairRunId = pairRunId,
                    EquipmentId = equipmentId,
                    OperationKey = operationKey,
                    CreateKey = $"SEC_CREATE_{suffix}",
                    ExecutionId = executionId,
                    ExecutionKey = $"SEC_EXEC_{suffix}",
                    RequestHash = new string('D', 64),
                });

            var captureSql = $"""
                EXECUTE AS USER=N'{rmsWriter}';
                EXEC dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE
                     @ExecutionId=@ExecutionId, @WorkScopeId=@WorkScopeId,
                     @PairRunId=@PairRunId, @SequenceRunId=@SequenceRunId,
                     @EquipmentId=@EquipmentId, @OperationKey=@OperationKey,
                     @RecipeId=N'SEC_RECIPE', @RecipeVersion=1,
                     @SnapshotSchema=@RecipeSchema, @SnapshotHash=@RecipeHash;
                REVERT;
                """;
            var recipeParameters = new
            {
                ExecutionId = executionId,
                WorkScopeId = workScopeId,
                PairRunId = pairRunId,
                SequenceRunId = sequenceRunId,
                EquipmentId = equipmentId,
                OperationKey = operationKey,
                RecipeSchema = recipeSchema,
                RecipeHash = recipeHash,
            };
            await database.ExecuteAsync(captureSql, recipeParameters);
            await database.ExecuteAsync(captureSql, recipeParameters);

            await using (var impersonationConnection = new SqlConnection(database.ConnectionString))
            {
                await impersonationConnection.OpenAsync();
                var invalidCapture = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsAsync(
                    impersonationConnection,
                    rmsWriter,
                    """
                    EXEC dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE
                         @ExecutionId=@ExecutionId, @WorkScopeId=@WorkScopeId,
                         @PairRunId=@PairRunId, @SequenceRunId=@SequenceRunId,
                         @EquipmentId=@EquipmentId, @OperationKey=@OperationKey,
                         @RecipeId=N'SEC_RECIPE', @RecipeVersion=1,
                         @SnapshotSchema=N'padded ', @SnapshotHash=@RecipeHash;
                    """,
                    recipeParameters));
                invalidCapture.Number.Should().Be(51624);
                (await impersonationConnection.ExecuteScalarAsync<string>("SELECT USER_NAME();"))
                    .Should().Be("dbo",
                        "a rejected impersonated call must restore the same SQL Server session");
            }

            var releaseSql = $"""
                EXECUTE AS USER=N'{sysWriter}';
                EXEC dbo.SYS_RELEASE_PROGRAM_ARTIFACT
                     @ArtifactId=@ArtifactId, @EquipmentId=@EquipmentId,
                     @OperationKey=@OperationKey, @ProductProfileId=N'security-profile',
                     @PluginId=N'plugin.security', @ProductDefinitionVersion=N'product-v1',
                     @ProgramVersion=N'program-v1', @ProgramSchema=@ProgramSchema,
                     @ProgramHash=@ProgramHash, @BoundRecipeSnapshotSchema=@RecipeSchema,
                     @BoundRecipeSnapshotHash=@RecipeHash, @ReleasedBy=N'business-releaser';
                REVERT;
                """;
            var programParameters = new
            {
                ArtifactId = artifactId,
                EquipmentId = equipmentId,
                OperationKey = operationKey,
                ProgramSchema = programSchema,
                ProgramHash = programHash,
                RecipeSchema = recipeSchema,
                RecipeHash = recipeHash,
            };
            await database.ExecuteAsync(releaseSql, programParameters);
            await database.ExecuteAsync(releaseSql, programParameters);
            await ExecuteAsAsync(database, sysWriter, """
                EXEC dbo.SYS_RELEASE_PROGRAM_ARTIFACT
                     @ArtifactId=@ArtifactId, @EquipmentId=@EquipmentId,
                     @OperationKey=@OperationKey, @ProductProfileId=N'security-profile',
                     @PluginId=N'plugin.security', @ProductDefinitionVersion=N'product-v1',
                     @ProgramVersion=N'program-v2', @ProgramSchema=@ProgramSchema,
                     @ProgramHash=@ProgramHash, @BoundRecipeSnapshotSchema=@RecipeSchema,
                     @BoundRecipeSnapshotHash=@RecipeHash, @ReleasedBy=N'business-releaser';
                """, new
                {
                    ArtifactId = rollingArtifactId,
                    EquipmentId = equipmentId,
                    OperationKey = operationKey,
                    ProgramSchema = programSchema,
                    ProgramHash = rollingProgramHash,
                    RecipeSchema = recipeSchema,
                    RecipeHash = recipeHash,
                });

            var invalidRelease = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsAsync(
                database,
                sysWriter,
                """
                EXEC dbo.SYS_RELEASE_PROGRAM_ARTIFACT
                     @ArtifactId=@ArtifactId, @EquipmentId=@EquipmentId,
                     @OperationKey=@OperationKey, @ProductProfileId=N'security-profile',
                     @PluginId=N'plugin.security', @ProductDefinitionVersion=N'product-v1',
                     @ProgramVersion=N'program-v1', @ProgramSchema=@ProgramSchema,
                     @ProgramHash=@InvalidHash, @BoundRecipeSnapshotSchema=@RecipeSchema,
                     @BoundRecipeSnapshotHash=@RecipeHash, @ReleasedBy=N'business-releaser';
                """,
                new
                {
                    ArtifactId = artifactId,
                    EquipmentId = equipmentId,
                    OperationKey = operationKey,
                    ProgramSchema = programSchema,
                    InvalidHash = programHash.ToLowerInvariant(),
                    RecipeSchema = recipeSchema,
                    RecipeHash = recipeHash,
                }));
            invalidRelease.Number.Should().Be(51625);

            (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE WHERE EXECUTION_ID=@id;",
                new { id = executionId })).Should().Be(1);
            (await database.ScalarAsync<string>(
                "SELECT CAPTURED_DATABASE_PRINCIPAL_NAME FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE WHERE EXECUTION_ID=@id;",
                new { id = executionId })).Should().Be(rmsWriter);
            (await database.ScalarAsync<string>(
                "SELECT RELEASED_DATABASE_PRINCIPAL_NAME FROM SYS_RELEASED_PROGRAM_ARTIFACT WHERE ARTIFACT_ID=@id;",
                new { id = artifactId })).Should().Be(sysWriter);

            await AssertDeniedAsync(database, rmsWriter,
                "INSERT INTO dbo.RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE DEFAULT VALUES;");
            await AssertDeniedAsync(database, sysWriter,
                "INSERT INTO dbo.SYS_RELEASED_PROGRAM_ARTIFACT DEFAULT VALUES;");
            await AssertDeniedAsync(database, runtime1,
                "INSERT INTO dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY DEFAULT VALUES;");
            await AssertDeniedAsync(database, runtime1,
                "SELECT COUNT(*) FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY;");
            await AssertDeniedAsync(database, runtime1,
                "SELECT COUNT(*) FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING;");
            (await HasPermissionAsync(database, rmsWriter, "dbo.SYS_RELEASE_PROGRAM_ARTIFACT", "EXECUTE"))
                .Should().Be(0);
            (await HasPermissionAsync(database, sysWriter,
                "dbo.RMS_CAPTURE_CANONICAL_RECIPE_EXECUTION_EVIDENCE", "EXECUTE")).Should().Be(0);

            var firstApplyArguments = new[]
            {
                "-RuntimeDatabaseUser", runtime1,
                "-RmsWriterDatabaseUser", rmsWriter,
                "-SysWriterDatabaseUser", sysWriter,
                "-EquipmentId", equipmentId,
                "-OperationKey", operationKey,
                "-ArtifactId", artifactId,
                "-ProductProfileId", "security-profile",
                "-PluginId", "plugin.security",
                "-ProductDefinitionVersion", "product-v1",
                "-ProgramVersion", "program-v1",
                "-ProgramSchema", programSchema,
                "-ProgramHash", programHash,
                "-BoundRecipeSnapshotSchema", recipeSchema,
                "-BoundRecipeSnapshotHash", recipeHash,
                "-Apply",
            };
            var firstApply = await RunCommissioningAsync(
                database.ConnectionString, firstApplyArguments);
            firstApply.ExitCode.Should().Be(0, firstApply.Output);
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneProjectionRuntime', @Runtime), 0);",
                new { Runtime = runtime1 })).Should().Be(1);

            var rollingApplyArguments = new[]
            {
                "-RuntimeDatabaseUser", runtime1,
                "-RmsWriterDatabaseUser", rmsWriter,
                "-SysWriterDatabaseUser", sysWriter,
                "-EquipmentId", equipmentId,
                "-OperationKey", operationKey,
                "-ArtifactId", rollingArtifactId,
                "-ProductProfileId", "security-profile",
                "-PluginId", "plugin.security",
                "-ProductDefinitionVersion", "product-v1",
                "-ProgramVersion", "program-v2",
                "-ProgramSchema", programSchema,
                "-ProgramHash", rollingProgramHash,
                "-BoundRecipeSnapshotSchema", recipeSchema,
                "-BoundRecipeSnapshotHash", recipeHash,
                "-Apply",
            };
            var rollingApply = await RunCommissioningAsync(
                database.ConnectionString, rollingApplyArguments);
            rollingApply.ExitCode.Should().Be(0, rollingApply.Output);
            await AssertDeniedAsync(database, runtime1,
                "INSERT INTO dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY DEFAULT VALUES;");
            await AssertDeniedAsync(database, runtime1,
                "SELECT COUNT(*) FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY;");
            await AssertDeniedAsync(database, runtime1,
                "SELECT COUNT(*) FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING;");

            await database.ExecuteAsync($"""
                ALTER ROLE NexaOneProjectionRuntime ADD MEMBER [{runtime2}];
                ALTER ROLE NexaOneProjectionRuntime ADD MEMBER [{unboundRuntime}];
                """);
            await database.ExecuteAsync(
                """
                INSERT INTO dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING
                    (DATABASE_PRINCIPAL_NAME, DATABASE_PRINCIPAL_SID, EQUIPMENT_ID, OPERATION_KEY,
                     ARTIFACT_ID, PRODUCT_PROFILE_ID, PLUGIN_ID, PRODUCT_DEFINITION_VERSION,
                     PROGRAM_VERSION, PROGRAM_SCHEMA, PROGRAM_HASH,
                     BOUND_RECIPE_SNAPSHOT_SCHEMA, BOUND_RECIPE_SNAPSHOT_HASH,
                     COMMISSIONED_AT, COMMISSIONED_BY)
                SELECT P.name, P.sid, @EquipmentId, @OperationKey, @ArtifactId,
                       N'security-profile', N'plugin.security', N'product-v1', N'program-v1',
                       @ProgramSchema, @ProgramHash, @RecipeSchema, @RecipeHash,
                       SYSUTCDATETIME(), ORIGINAL_LOGIN()
                  FROM sys.database_principals P
                 WHERE P.name=@Runtime;
                """,
                new
                {
                    Runtime = runtime2,
                    EquipmentId = equipmentId,
                    OperationKey = operationKey,
                    ArtifactId = artifactId,
                    ProgramSchema = programSchema,
                    ProgramHash = programHash,
                    RecipeSchema = recipeSchema,
                    RecipeHash = recipeHash,
                });
            (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING WHERE DATABASE_PRINCIPAL_NAME=@name;",
                new { name = runtime1 })).Should().Be(2,
                "rolling upgrades retain the previous artifact binding for recovery");

            var authoritySql = """
                EXEC dbo.POM_INSERT_WORK_SCOPE_PROJECTION_AUTHORITY
                     @WorkScopeId=@WorkScopeId, @SourceClientId=@SourceClientId,
                     @EquipmentId=@EquipmentId, @OperationKey=@OperationKey,
                     @PairRunId=@PairRunId, @SequenceRunId=@SequenceRunId,
                     @RecipeExecutionId=@ExecutionId, @RecipeId=N'SEC_RECIPE', @RecipeVersion=1,
                     @RecipeSnapshotSchema=@RecipeSchema, @RecipeSnapshotHash=@RecipeHash,
                     @ProgramArtifactId=@ArtifactId, @ProgramSchema=@ProgramSchema,
                     @ProgramHash=@ProgramHash, @ProvisionIdempotencyKey=@IdempotencyKey,
                     @ProvisionRequestHash=@RequestHash, @ProvisionedBy=N'business-actor';
                """;
            var authorityParameters = new
            {
                WorkScopeId = workScopeId,
                SourceClientId = $"SEC_SOURCE_{suffix}",
                EquipmentId = equipmentId,
                OperationKey = operationKey,
                PairRunId = pairRunId,
                SequenceRunId = sequenceRunId,
                ExecutionId = executionId,
                RecipeSchema = recipeSchema,
                RecipeHash = recipeHash,
                ArtifactId = artifactId,
                ProgramSchema = programSchema,
                ProgramHash = programHash,
                IdempotencyKey = $"SEC_AUTH_{suffix}",
                RequestHash = new string('C', 64),
            };
            await ExecuteAsAsync(database, runtime1, authoritySql, authorityParameters);
            await ExecuteAsAsync(database, runtime1, authoritySql, authorityParameters);
            await ExecuteAsAsync(database, runtime2, authoritySql, authorityParameters);
            await ExecuteAsAsync(database, runtime1, """
                EXEC dbo.POM_GET_ACTIVE_PROJECTION_AUTHORITY_FOR_UPDATE @WorkScopeId=@WorkScopeId;
                """, new { WorkScopeId = workScopeId });

            var invalidAuthority = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsAsync(
                database,
                runtime1,
                authoritySql,
                new
                {
                    WorkScopeId = workScopeId,
                    SourceClientId = new string('X', 101),
                    EquipmentId = equipmentId,
                    OperationKey = operationKey,
                    PairRunId = pairRunId,
                    SequenceRunId = sequenceRunId,
                    ExecutionId = executionId,
                    RecipeSchema = recipeSchema,
                    RecipeHash = recipeHash,
                    ArtifactId = artifactId,
                    ProgramSchema = programSchema,
                    ProgramHash = programHash,
                    IdempotencyKey = $"SEC_AUTH_BAD_{suffix}",
                    RequestHash = new string('C', 64),
                }));
            invalidAuthority.Number.Should().Be(51627);

            (await database.ScalarAsync<string>(
                "SELECT PROVISIONED_DATABASE_PRINCIPAL_NAME FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY WHERE WORK_SCOPE_ID=@id;",
                new { id = workScopeId })).Should().Be(runtime1,
                "credential rotation replay must preserve first provisioning provenance");
            (await ActiveAuthorityCountAsync(database, runtime1, workScopeId)).Should().Be(1);
            (await ActiveAuthorityCountAsync(database, runtime2, workScopeId)).Should().Be(1);
            (await ActiveAuthorityCountAsync(database, unboundRuntime, workScopeId)).Should().Be(0);
            (await ExecuteScalarAsAsync(database, runtime1,
                "(SELECT COUNT(*) FROM dbo.POM_PROJECTION_AUTHORITY_SCOPE_FENCE WHERE WORK_SCOPE_ID=@id)",
                new { id = workScopeId })).Should().Be(1);
            await database.ExecuteAsync(
                "UPDATE dbo.POM_WORK_SCOPE SET VERSION_NO=2 WHERE WORK_SCOPE_ID=@WorkScopeId;",
                new { WorkScopeId = workScopeId });
            await AssertDeniedAsync(database, runtime1, """
                UPDATE dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY
                   SET LAST_APPLIED_VERSION_NO=2,
                       LAST_APPLIED_AT=SYSUTCDATETIME()
                 WHERE WORK_SCOPE_ID=@WorkScopeId AND LAST_APPLIED_VERSION_NO=1;
                """, new { WorkScopeId = workScopeId });
            (await AdvanceLineageWhileDecommissionWaitsAsync(
                database, runtime2, workScopeId, artifactId, 1, 2)).Should().Be(1,
                "lineage holds its active binding lock until the caller transaction commits");
            (await AdvanceLineageAsync(database, runtime1, workScopeId, 1, 3)).Should().Be(0,
                "a stale expected lineage cannot advance authority");
            (await AdvanceLineageAsync(database, unboundRuntime, workScopeId, 2, 3)).Should().Be(0,
                "an unbound runtime cannot advance authority");
            await database.ExecuteAsync(
                "UPDATE dbo.POM_WORK_SCOPE SET VERSION_NO=3 WHERE WORK_SCOPE_ID=@WorkScopeId;",
                new { WorkScopeId = workScopeId });
            (await AdvanceLineageAsync(database, runtime1, workScopeId, 2, 3)).Should().Be(1,
                "a standalone call must own one atomic authority/artifact/binding/update transaction");
            (await database.ScalarAsync<int>("""
                SELECT LAST_APPLIED_VERSION_NO
                  FROM dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY
                 WHERE WORK_SCOPE_ID=@WorkScopeId;
                """, new { WorkScopeId = workScopeId })).Should().Be(3);

            var unbound = await Assert.ThrowsAsync<SqlException>(() =>
                ExecuteAsAsync(database, unboundRuntime, authoritySql, authorityParameters));
            unbound.Number.Should().Be(51613);

            await database.ExecuteAsync(
                "DELETE FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING WHERE DATABASE_PRINCIPAL_NAME=@name;",
                new { name = runtime2 });
            (await ActiveAuthorityCountAsync(database, runtime2, workScopeId)).Should().Be(0);
            (await AdvanceLineageAsync(database, runtime2, workScopeId, 3, 4)).Should().Be(0,
                "binding decommission is also a lineage-commit stop boundary");
            var decommissioned = await Assert.ThrowsAsync<SqlException>(() =>
                ExecuteAsAsync(database, runtime2, authoritySql, authorityParameters));
            decommissioned.Number.Should().Be(51613);

            await ExecuteAsAsync(database, sysWriter, """
                EXEC dbo.SYS_REVOKE_PROGRAM_ARTIFACT
                     @RevocationId=@RevocationId, @ArtifactId=@ArtifactId,
                     @RevokedBy=N'business-releaser', @Reason=N'security contract';
                """, new { RevocationId = $"SEC_REV_{suffix}", ArtifactId = artifactId });
            var invalidRevocation = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsAsync(
                database,
                sysWriter,
                """
                EXEC dbo.SYS_REVOKE_PROGRAM_ARTIFACT
                     @RevocationId=N'bad-revocation', @ArtifactId=@ArtifactId,
                     @RevokedBy=N'business-releaser', @Reason=@Reason;
                """,
                new { ArtifactId = artifactId, Reason = "control\ncharacter" }));
            invalidRevocation.Number.Should().Be(51626);
            (await ActiveAuthorityCountAsync(database, runtime1, workScopeId)).Should().Be(1,
                "revocation blocks new authority but does not break existing recovery/replay");
            await ExecuteAsAsync(database, runtime1, authoritySql, authorityParameters);
            (await database.ScalarAsync<string>(
                "SELECT REVOKED_DATABASE_PRINCIPAL_NAME FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION WHERE ARTIFACT_ID=@id;",
                new { id = artifactId })).Should().Be(sysWriter);

            var decommissionArguments = new[]
            {
                "-RuntimeDatabaseUser", runtime1,
                "-ArtifactId", artifactId,
                "-Decommission",
            };
            var decommission = await RunCommissioningAsync(
                database.ConnectionString, decommissionArguments);
            decommission.ExitCode.Should().Be(0, decommission.Output);
            using (var evidence = JsonDocument.Parse(decommission.EvidenceJson))
            {
                evidence.RootElement.GetProperty("Success").GetBoolean().Should().BeTrue();
                var removed = evidence.RootElement.GetProperty("RemovedBindings");
                removed.GetArrayLength().Should().Be(1);
                removed[0].GetProperty("ProgramHash").GetString().Should().Be(programHash);
            }
            var idempotentDecommission = await RunCommissioningAsync(
                database.ConnectionString, decommissionArguments);
            idempotentDecommission.ExitCode.Should().Be(0, idempotentDecommission.Output);
            using (var evidence = JsonDocument.Parse(idempotentDecommission.EvidenceJson))
                evidence.RootElement.GetProperty("RemovedBindings").GetArrayLength().Should().Be(0);
            (await AdvanceLineageAsync(database, runtime1, workScopeId, 3, 4)).Should().Be(0,
                "lineage cannot advance after the authority artifact binding is removed");

            await database.ExecuteAsync($"""
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{runtime2}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{runtime2}];
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{unboundRuntime}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{unboundRuntime}];
                """);
            var validateArguments = new[]
            {
                "-RuntimeDatabaseUser", runtime1,
                "-RmsWriterDatabaseUser", rmsWriter,
                "-SysWriterDatabaseUser", sysWriter,
                "-EquipmentId", equipmentId,
                "-OperationKey", operationKey,
                "-ArtifactId", rollingArtifactId,
                "-ProductProfileId", "security-profile",
                "-PluginId", "plugin.security",
                "-ProductDefinitionVersion", "product-v1",
                "-ProgramVersion", "program-v2",
                "-ProgramSchema", programSchema,
                "-ProgramHash", rollingProgramHash,
                "-BoundRecipeSnapshotSchema", recipeSchema,
                "-BoundRecipeSnapshotHash", recipeHash,
                "-ValidateOnly",
            };
            var cleanValidation = await RunCommissioningAsync(
                database.ConnectionString, validateArguments);
            cleanValidation.ExitCode.Should().Be(0, cleanValidation.Output);

            await database.ExecuteAsync($"""
                CREATE PROCEDURE dbo.[{rogueHelperProcedure}]
                WITH EXECUTE AS OWNER
                AS
                BEGIN
                  SET NOCOUNT ON;
                  DECLARE @sql NVARCHAR(MAX)=
                    N'SELECT COUNT_BIG(*) FROM dbo.' + N'POM_WORK_SCOPE_' + N'PROJECTION_AUTHORITY;';
                  EXEC(@sql);
                END;
                """);
            await database.ExecuteAsync($"""
                CREATE PROCEDURE dbo.[{rogueProcedure}]
                AS
                BEGIN
                  SET NOCOUNT ON;
                  EXEC dbo.[{rogueHelperProcedure}];
                END;
                """);
            await database.ExecuteAsync(
                $"GRANT EXECUTE ON OBJECT::dbo.[{rogueProcedure}] TO [{runtime1}];");
            var rogueValidation = await RunCommissioningAsync(
                database.ConnectionString, validateArguments);
            rogueValidation.ExitCode.Should().NotBe(0);
            rogueValidation.Output.Should().Contain("Unexpected trusted-table module");
            await database.ExecuteAsync($"DROP PROCEDURE dbo.[{rogueProcedure}];");
            await database.ExecuteAsync($"DROP PROCEDURE dbo.[{rogueHelperProcedure}];");

            await database.ExecuteAsync($"""
                CREATE USER [{impersonator}] WITHOUT LOGIN;
                GRANT IMPERSONATE ON USER::[{rmsWriter}] TO [{impersonator}];
                """);
            var impersonationValidation = await RunCommissioningAsync(
                database.ConnectionString, validateArguments);
            impersonationValidation.ExitCode.Should().NotBe(0);
            impersonationValidation.Output.Should().Contain("EXECUTE/IMPERSONATE GRANT");
            await database.ExecuteAsync($"DROP USER [{impersonator}];");

            await database.ExecuteAsync(
                $"CREATE SCHEMA [{rogueSchema}] AUTHORIZATION dbo;");
            await database.ExecuteAsync(
                $"GRANT EXECUTE ON SCHEMA::[{rogueSchema}] TO [{runtime1}];");
            var sameOwnerSchemaValidation = await RunCommissioningAsync(
                database.ConnectionString, validateArguments);
            sameOwnerSchemaValidation.ExitCode.Should().NotBe(0);
            sameOwnerSchemaValidation.Output.Should().Contain("EXECUTE/IMPERSONATE GRANT");
            await database.ExecuteAsync($"DROP SCHEMA [{rogueSchema}];");

            await database.ExecuteAsync($"""
                CREATE SERVER ROLE [{rogueServerRole}];
                GRANT CONTROL SERVER TO [{rogueServerRole}];
                """);
            var broadServerGrantValidation = await RunCommissioningAsync(
                database.ConnectionString, validateArguments);
            broadServerGrantValidation.ExitCode.Should().NotBe(0);
            broadServerGrantValidation.Output.Should().Contain($"server:{rogueServerRole}");
            await database.ExecuteAsync($"""
                REVOKE CONTROL SERVER FROM [{rogueServerRole}];
                DROP SERVER ROLE [{rogueServerRole}];
                """);

            await database.ExecuteAsync(
                $"CREATE SYNONYM dbo.[{rogueSynonym}] FOR dbo.POM_WORK_SCOPE_PROJECTION_AUTHORITY;");
            var synonymValidation = await RunCommissioningAsync(
                database.ConnectionString, validateArguments);
            synonymValidation.ExitCode.Should().NotBe(0);
            synonymValidation.Output.Should().Contain("trusted-table synonym");
            await database.ExecuteAsync($"DROP SYNONYM dbo.[{rogueSynonym}];");

            await database.ExecuteAsync($"""
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{runtime1}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{runtime1}];
                IF IS_ROLEMEMBER(N'NexaOneRmsEvidenceWriter', N'{rmsWriter}')=1
                  ALTER ROLE NexaOneRmsEvidenceWriter DROP MEMBER [{rmsWriter}];
                IF IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', N'{sysWriter}')=1
                  ALTER ROLE NexaOneSysReleaseWriter DROP MEMBER [{sysWriter}];
                DROP USER [{runtime1}];
                DROP USER [{rmsWriter}];
                DROP USER [{sysWriter}];
                """);
            var missingCredentialDecommission = await RunCommissioningAsync(
                database.ConnectionString,
                new[]
                {
                    "-RuntimeDatabaseUser", runtime1,
                    "-Decommission", "-DecommissionAllBindings",
                });
            missingCredentialDecommission.ExitCode.Should().Be(0, missingCredentialDecommission.Output);
            using (var evidence = JsonDocument.Parse(missingCredentialDecommission.EvidenceJson))
            {
                evidence.RootElement.GetProperty("RemovedBindings").GetArrayLength().Should().Be(1);
                evidence.RootElement.GetProperty("SecurityAuditDisposition").GetString()
                    .Should().Contain("Fail-safe decommission");
            }
        }
        finally
        {
            await database.ExecuteAsync($"""
                IF OBJECT_ID(N'dbo.{rogueProcedure}', N'P') IS NOT NULL
                  DROP PROCEDURE dbo.[{rogueProcedure}];
                IF OBJECT_ID(N'dbo.{rogueHelperProcedure}', N'P') IS NOT NULL
                  DROP PROCEDURE dbo.[{rogueHelperProcedure}];
                IF OBJECT_ID(N'dbo.{rogueSynonym}', N'SN') IS NOT NULL
                  DROP SYNONYM dbo.[{rogueSynonym}];
                IF SCHEMA_ID(N'{rogueSchema}') IS NOT NULL
                  DROP SCHEMA [{rogueSchema}];
                IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name=N'{rogueServerRole}' AND type='R')
                BEGIN
                  REVOKE CONTROL SERVER FROM [{rogueServerRole}];
                  DROP SERVER ROLE [{rogueServerRole}];
                END;
                IF DATABASE_PRINCIPAL_ID(N'{impersonator}') IS NOT NULL DROP USER [{impersonator}];
                DELETE FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING
                 WHERE DATABASE_PRINCIPAL_NAME IN (N'{runtime1}', N'{runtime2}', N'{unboundRuntime}');
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{runtime1}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{runtime1}];
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{runtime2}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{runtime2}];
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{unboundRuntime}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{unboundRuntime}];
                IF IS_ROLEMEMBER(N'NexaOneRmsEvidenceWriter', N'{rmsWriter}')=1
                  ALTER ROLE NexaOneRmsEvidenceWriter DROP MEMBER [{rmsWriter}];
                IF IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', N'{sysWriter}')=1
                  ALTER ROLE NexaOneSysReleaseWriter DROP MEMBER [{sysWriter}];
                IF DATABASE_PRINCIPAL_ID(N'{runtime1}') IS NOT NULL DROP USER [{runtime1}];
                IF DATABASE_PRINCIPAL_ID(N'{runtime2}') IS NOT NULL DROP USER [{runtime2}];
                IF DATABASE_PRINCIPAL_ID(N'{unboundRuntime}') IS NOT NULL DROP USER [{unboundRuntime}];
                IF DATABASE_PRINCIPAL_ID(N'{rmsWriter}') IS NOT NULL DROP USER [{rmsWriter}];
                IF DATABASE_PRINCIPAL_ID(N'{sysWriter}') IS NOT NULL DROP USER [{sysWriter}];
                """);
        }
    }

    [Fact]
    public async Task Historical_release_provenance_requires_exact_approval_after_SYS_writer_rotation()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(_output);
        if (database is null)
            return;

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var runtime = $"sec_rotate_runtime_{suffix}";
        var nextRuntime = $"sec_rotate_runtime2_{suffix}";
        var rmsWriter = $"sec_rotate_rms_{suffix}";
        var oldSysWriter = $"sec_rotate_sys1_{suffix}";
        var currentSysWriter = $"sec_rotate_sys2_{suffix}";
        var equipmentId = $"SEC_ROTATE_EQ_{suffix}";
        var operationKey = $"SEC_ROTATE_OP_{suffix}";
        var artifactId = $"SEC_ROTATE_ART_{suffix}";
        var programSchema = "security-rotation-program-v1";
        var recipeSchema = "security-rotation-recipe-v1";
        var programHash = new string('7', 64);
        var recipeHash = new string('8', 64);

        string[] FullArguments(string runtimeUser, string mode, string? approvedReleaseSid = null)
        {
            var arguments = new List<string>
            {
                "-RuntimeDatabaseUser", runtimeUser,
                "-RmsWriterDatabaseUser", rmsWriter,
                "-SysWriterDatabaseUser", currentSysWriter,
                "-EquipmentId", equipmentId,
                "-OperationKey", operationKey,
                "-ArtifactId", artifactId,
                "-ProductProfileId", "security-rotation-profile",
                "-PluginId", "plugin.security.rotation",
                "-ProductDefinitionVersion", "product-rotation-v1",
                "-ProgramVersion", "program-rotation-v1",
                "-ProgramSchema", programSchema,
                "-ProgramHash", programHash,
                "-BoundRecipeSnapshotSchema", recipeSchema,
                "-BoundRecipeSnapshotHash", recipeHash,
                mode,
            };
            if (approvedReleaseSid is not null)
            {
                arguments.Add("-ApprovedReleasePrincipalSidSha256");
                arguments.Add(approvedReleaseSid);
            }
            return arguments.ToArray();
        }

        var releaseSql = """
            EXEC dbo.SYS_RELEASE_PROGRAM_ARTIFACT
                 @ArtifactId=@ArtifactId, @EquipmentId=@EquipmentId,
                 @OperationKey=@OperationKey,
                 @ProductProfileId=N'security-rotation-profile',
                 @PluginId=N'plugin.security.rotation',
                 @ProductDefinitionVersion=N'product-rotation-v1',
                 @ProgramVersion=N'program-rotation-v1', @ProgramSchema=@ProgramSchema,
                 @ProgramHash=@ProgramHash, @BoundRecipeSnapshotSchema=@RecipeSchema,
                 @BoundRecipeSnapshotHash=@RecipeHash, @ReleasedBy=N'rotation-releaser';
            """;
        var releaseParameters = new
        {
            ArtifactId = artifactId,
            EquipmentId = equipmentId,
            OperationKey = operationKey,
            ProgramSchema = programSchema,
            ProgramHash = programHash,
            RecipeSchema = recipeSchema,
            RecipeHash = recipeHash,
        };

        await database.ExecuteAsync($"""
            CREATE USER [{runtime}] WITHOUT LOGIN;
            CREATE USER [{nextRuntime}] WITHOUT LOGIN;
            CREATE USER [{rmsWriter}] WITHOUT LOGIN;
            CREATE USER [{oldSysWriter}] WITHOUT LOGIN;
            CREATE USER [{currentSysWriter}] WITHOUT LOGIN;
            """);
        try
        {
            var oldWriterBootstrap = await RunCommissioningAsync(
                database.ConnectionString,
                new[]
                {
                    "-RuntimeDatabaseUser", runtime,
                    "-RmsWriterDatabaseUser", rmsWriter,
                    "-SysWriterDatabaseUser", oldSysWriter,
                    "-Apply", "-WriterBootstrapOnly",
                });
            oldWriterBootstrap.ExitCode.Should().Be(0, oldWriterBootstrap.Output);
            await ExecuteAsAsync(database, oldSysWriter, releaseSql, releaseParameters);
            var historicalReleaseSid = await database.ScalarAsync<string>(
                """
                SELECT CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', RELEASED_DATABASE_PRINCIPAL_SID), 2)
                  FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT
                 WHERE ARTIFACT_ID=@ArtifactId;
                """,
                new { ArtifactId = artifactId });
            historicalReleaseSid.Should().MatchRegex("^[0-9A-F]{64}$");

            var currentWriterBootstrap = await RunCommissioningAsync(
                database.ConnectionString,
                new[]
                {
                    "-RuntimeDatabaseUser", runtime,
                    "-RmsWriterDatabaseUser", rmsWriter,
                    "-SysWriterDatabaseUser", currentSysWriter,
                    "-Apply", "-WriterBootstrapOnly",
                });
            currentWriterBootstrap.ExitCode.Should().Be(0, currentWriterBootstrap.Output);
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', @Writer), 0);",
                new { Writer = oldSysWriter })).Should().Be(0);
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', @Writer), 0);",
                new { Writer = currentSysWriter })).Should().Be(1);
            await AssertDeniedAsync(database, oldSysWriter, releaseSql, releaseParameters);
            await database.ExecuteAsync($"DROP USER [{oldSysWriter}];");

            var lowercaseApproval = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-Apply", historicalReleaseSid.ToLowerInvariant()));
            lowercaseApproval.ExitCode.Should().NotBe(0);
            lowercaseApproval.Output.Should().Contain("exact uppercase SHA-256 hex");

            var missingApproval = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-Apply"));
            missingApproval.ExitCode.Should().NotBe(0);
            missingApproval.Output.Should().Contain("historical release provenance requires");
            using (var evidence = JsonDocument.Parse(missingApproval.EvidenceJson))
            {
                var release = evidence.RootElement.GetProperty("ReleaseProvenance");
                release.GetProperty("PrincipalName").GetString().Should().Be(oldSysWriter);
                release.GetProperty("PrincipalSidSha256").GetString().Should().Be(historicalReleaseSid);
                release.GetProperty("MatchesCurrentSysWriter").GetBoolean().Should().BeFalse();
                release.GetProperty("ExistingExactBinding").GetBoolean().Should().BeFalse();
                release.GetProperty("HistoricalApprovalRequired").GetBoolean().Should().BeTrue();
                release.GetProperty("HistoricalApprovalProvided").GetBoolean().Should().BeFalse();
                release.GetProperty("HistoricalApprovalMatched").GetBoolean().Should().BeFalse();
                release.TryGetProperty("PrincipalSid", out _).Should().BeFalse(
                    "commissioning evidence must never expose the raw database SID");
            }
            (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING WHERE DATABASE_PRINCIPAL_NAME=@Runtime;",
                new { Runtime = runtime })).Should().Be(0);
            (await database.ScalarAsync<int>(
                "SELECT ISNULL(IS_ROLEMEMBER(N'NexaOneProjectionRuntime', @Runtime), 0);",
                new { Runtime = runtime })).Should().Be(0,
                "failed historical approval must roll back the full Apply");

            var wrongApproval = historicalReleaseSid[0] == 'F'
                ? "E" + historicalReleaseSid[1..]
                : "F" + historicalReleaseSid[1..];
            var mismatchedApproval = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-Apply", wrongApproval));
            mismatchedApproval.ExitCode.Should().NotBe(0);
            mismatchedApproval.Output.Should().Contain("does not match the server-read");
            mismatchedApproval.Output.Should().Contain("historical release principal SID");
            using (var evidence = JsonDocument.Parse(mismatchedApproval.EvidenceJson))
            {
                var release = evidence.RootElement.GetProperty("ReleaseProvenance");
                release.GetProperty("HistoricalApprovalProvided").GetBoolean().Should().BeTrue();
                release.GetProperty("HistoricalApprovalMatched").GetBoolean().Should().BeFalse();
            }

            var approvedApply = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-Apply", historicalReleaseSid));
            approvedApply.ExitCode.Should().Be(0, approvedApply.Output);
            using (var evidence = JsonDocument.Parse(approvedApply.EvidenceJson))
            {
                var release = evidence.RootElement.GetProperty("ReleaseProvenance");
                release.GetProperty("PrincipalName").GetString().Should().Be(oldSysWriter);
                release.GetProperty("PrincipalSidSha256").GetString().Should().Be(historicalReleaseSid);
                release.GetProperty("HistoricalApprovalRequired").GetBoolean().Should().BeTrue();
                release.GetProperty("HistoricalApprovalMatched").GetBoolean().Should().BeTrue();
            }
            (await database.ScalarAsync<string>(
                "SELECT RELEASED_DATABASE_PRINCIPAL_NAME FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT WHERE ARTIFACT_ID=@ArtifactId;",
                new { ArtifactId = artifactId })).Should().Be(oldSysWriter,
                "binding approval must never rewrite immutable release provenance");

            var idempotentApply = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-Apply"));
            idempotentApply.ExitCode.Should().Be(0, idempotentApply.Output);
            using (var evidence = JsonDocument.Parse(idempotentApply.EvidenceJson))
            {
                var release = evidence.RootElement.GetProperty("ReleaseProvenance");
                release.GetProperty("ExistingExactBinding").GetBoolean().Should().BeTrue();
                release.GetProperty("HistoricalApprovalRequired").GetBoolean().Should().BeFalse();
            }

            var validateOnly = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-ValidateOnly"));
            validateOnly.ExitCode.Should().Be(0, validateOnly.Output);
            using (var evidence = JsonDocument.Parse(validateOnly.EvidenceJson))
            {
                var release = evidence.RootElement.GetProperty("ReleaseProvenance");
                release.GetProperty("PrincipalName").GetString().Should().Be(oldSysWriter);
                release.GetProperty("MatchesCurrentSysWriter").GetBoolean().Should().BeFalse();
                release.GetProperty("HistoricalApprovalRequired").GetBoolean().Should().BeFalse();
            }

            await ExecuteAsAsync(database, currentSysWriter, releaseSql, releaseParameters);
            (await database.ScalarAsync<string>(
                "SELECT RELEASED_DATABASE_PRINCIPAL_NAME FROM dbo.SYS_RELEASED_PROGRAM_ARTIFACT WHERE ARTIFACT_ID=@ArtifactId;",
                new { ArtifactId = artifactId })).Should().Be(oldSysWriter,
                "an exact replay by the rotated writer preserves first release provenance");
            await ExecuteAsAsync(
                database,
                currentSysWriter,
                """
                EXEC dbo.SYS_REVOKE_PROGRAM_ARTIFACT
                     @RevocationId=@RevocationId, @ArtifactId=@ArtifactId,
                     @RevokedBy=N'rotation-releaser', @Reason=N'rotation revocation contract';
                """,
                new { RevocationId = $"SEC_ROTATE_REV_{suffix}", ArtifactId = artifactId });

            var revokedValidate = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(runtime, "-ValidateOnly"));
            revokedValidate.ExitCode.Should().NotBe(0,
                "worker activation validation must fail closed after a bound artifact is revoked");
            using (var evidence = JsonDocument.Parse(revokedValidate.EvidenceJson))
            {
                evidence.RootElement.GetProperty("Success").GetBoolean().Should().BeFalse();
                evidence.RootElement.GetProperty("Error").GetString().Should()
                    .Contain("revoked program artifact cannot be commissioned");
            }

            var revokedApply = await RunCommissioningAsync(
                database.ConnectionString,
                FullArguments(nextRuntime, "-Apply", historicalReleaseSid));
            revokedApply.ExitCode.Should().NotBe(0);
            using (var evidence = JsonDocument.Parse(revokedApply.EvidenceJson))
            {
                evidence.RootElement.GetProperty("Success").GetBoolean().Should().BeFalse();
                evidence.RootElement.GetProperty("Error").GetString().Should()
                    .Contain("revoked program artifact cannot be commissioned");
            }
            (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING WHERE DATABASE_PRINCIPAL_NAME=@Runtime;",
                new { Runtime = nextRuntime })).Should().Be(0,
                "historical approval must never override revocation");
        }
        finally
        {
            await database.ExecuteAsync($"""
                DELETE FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING
                 WHERE DATABASE_PRINCIPAL_NAME IN (N'{runtime}', N'{nextRuntime}');
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{runtime}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{runtime}];
                IF IS_ROLEMEMBER(N'NexaOneProjectionRuntime', N'{nextRuntime}')=1
                  ALTER ROLE NexaOneProjectionRuntime DROP MEMBER [{nextRuntime}];
                IF IS_ROLEMEMBER(N'NexaOneRmsEvidenceWriter', N'{rmsWriter}')=1
                  ALTER ROLE NexaOneRmsEvidenceWriter DROP MEMBER [{rmsWriter}];
                IF DATABASE_PRINCIPAL_ID(N'{oldSysWriter}') IS NOT NULL
                   AND IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', N'{oldSysWriter}')=1
                  ALTER ROLE NexaOneSysReleaseWriter DROP MEMBER [{oldSysWriter}];
                IF IS_ROLEMEMBER(N'NexaOneSysReleaseWriter', N'{currentSysWriter}')=1
                  ALTER ROLE NexaOneSysReleaseWriter DROP MEMBER [{currentSysWriter}];
                IF DATABASE_PRINCIPAL_ID(N'{runtime}') IS NOT NULL DROP USER [{runtime}];
                IF DATABASE_PRINCIPAL_ID(N'{nextRuntime}') IS NOT NULL DROP USER [{nextRuntime}];
                IF DATABASE_PRINCIPAL_ID(N'{rmsWriter}') IS NOT NULL DROP USER [{rmsWriter}];
                IF DATABASE_PRINCIPAL_ID(N'{oldSysWriter}') IS NOT NULL DROP USER [{oldSysWriter}];
                IF DATABASE_PRINCIPAL_ID(N'{currentSysWriter}') IS NOT NULL DROP USER [{currentSysWriter}];
                """);
        }
    }

    private static async Task ExecuteAsAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string sql,
        object? parameters = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsAsync(connection, databaseUser, sql, parameters);
    }

    private static async Task ExecuteAsAsync(
        SqlConnection connection,
        string databaseUser,
        string sql,
        object? parameters = null)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"EXECUTE AS USER=N'{databaseUser}';",
            commandTimeout: 60));
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                parameters,
                commandTimeout: 60));
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "REVERT;",
                commandTimeout: 60));
        }
    }

    private static async Task AssertDeniedAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string sql,
        object? parameters = null)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsAsync(database, databaseUser, sql, parameters));
        exception.Number.Should().Be(229);
    }

    private static Task<int> ActiveAuthorityCountAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string workScopeId) => database.ScalarAsync<int>(
        $"""
        DECLARE @result INT;
        EXECUTE AS USER=N'{databaseUser}';
        SELECT @result=COUNT(*) FROM dbo.POM_ACTIVE_PROJECTION_RUNTIME_AUTHORITY
         WHERE WORK_SCOPE_ID=@WorkScopeId;
        REVERT;
        SELECT @result;
        """,
        new { WorkScopeId = workScopeId });

    private static Task<int> HasPermissionAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string securable,
        string permission) => ExecuteScalarAsAsync(
        database,
        databaseUser,
        "HAS_PERMS_BY_NAME(@securable, N'OBJECT', @permission)",
        new { securable, permission });

    private static Task<int> AdvanceLineageAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string workScopeId,
        int expectedVersion,
        int resultVersion) => database.ScalarAsync<int>(
        $"""
        DECLARE @result TABLE (AffectedRows INT NOT NULL);
        EXECUTE AS USER=N'{databaseUser}';
        INSERT INTO @result (AffectedRows)
          EXEC dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE
               @WorkScopeId=@WorkScopeId,
               @ExpectedVersion=@ExpectedVersion,
               @ResultVersion=@ResultVersion;
        REVERT;
        SELECT COALESCE(MAX(AffectedRows), 0) FROM @result;
        """,
        new { WorkScopeId = workScopeId, ExpectedVersion = expectedVersion, ResultVersion = resultVersion });

    private static async Task<int> AdvanceLineageWhileDecommissionWaitsAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string workScopeId,
        string artifactId,
        int expectedVersion,
        int resultVersion)
    {
        await using var runtimeConnection = new SqlConnection(database.ConnectionString);
        await runtimeConnection.OpenAsync();
        await using var runtimeTransaction =
            (SqlTransaction)await runtimeConnection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await using var advance = runtimeConnection.CreateCommand();
        advance.Transaction = runtimeTransaction;
        advance.CommandText = $"""
            DECLARE @result TABLE (AffectedRows INT NOT NULL);
            EXECUTE AS USER=N'{databaseUser}';
            INSERT INTO @result (AffectedRows)
              EXEC dbo.POM_ADVANCE_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE
                   @WorkScopeId=@WorkScopeId,
                   @ExpectedVersion=@ExpectedVersion,
                   @ResultVersion=@ResultVersion;
            REVERT;
            SELECT COALESCE(MAX(AffectedRows), 0) FROM @result;
            """;
        advance.Parameters.AddWithValue("@WorkScopeId", workScopeId);
        advance.Parameters.AddWithValue("@ExpectedVersion", expectedVersion);
        advance.Parameters.AddWithValue("@ResultVersion", resultVersion);
        var affected = Convert.ToInt32(await advance.ExecuteScalarAsync());

        var decommission = database.ExecuteAsync(
            """
            DELETE FROM dbo.POM_PROJECTION_RUNTIME_PRODUCT_BINDING
             WHERE DATABASE_PRINCIPAL_NAME=@Runtime
               AND ARTIFACT_ID=@ArtifactId;
            """,
            new { Runtime = databaseUser, ArtifactId = artifactId });
        await Task.Delay(200);
        decommission.IsCompleted.Should().BeFalse(
            "binding deletion must wait behind the caller-owned lineage transaction");

        await runtimeTransaction.CommitAsync();
        await decommission.WaitAsync(TimeSpan.FromSeconds(15));
        return affected;
    }

    private static async Task<CommissioningResult> RunCommissioningAsync(
        string connectionString,
        IReadOnlyList<string> arguments)
    {
        var evidencePath = Path.Combine(
            Path.GetTempPath(), $"nexa-v160-mssql-{Guid.NewGuid():N}.json");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                     RepositorySource.GetFile("tools", "ops", "Set-TrustedAuthoritySecurity.ps1"),
                     "-ConnectionString", connectionString,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-EvidencePath");
        startInfo.ArgumentList.Add(evidencePath);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start trusted-authority commissioning.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await stdout;
            var standardError = await stderr;
            var output = standardOutput + Environment.NewLine + standardError;
            var evidenceJson = File.Exists(evidencePath)
                ? await File.ReadAllTextAsync(evidencePath, timeout.Token)
                : "{}";
            return new CommissioningResult(process.ExitCode, output, evidenceJson);
        }
        finally
        {
            if (File.Exists(evidencePath))
                File.Delete(evidencePath);
        }
    }

    private sealed record CommissioningResult(int ExitCode, string Output, string EvidenceJson);

    private static Task<int> ExecuteScalarAsAsync(
        MssqlContractDatabase database,
        string databaseUser,
        string scalarExpression,
        object? parameters = null) => database.ScalarAsync<int>(
        $"""
        DECLARE @result INT;
        EXECUTE AS USER=N'{databaseUser}';
        SELECT @result={scalarExpression};
        REVERT;
        SELECT @result;
        """,
        parameters);
}
