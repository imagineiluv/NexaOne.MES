using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.RMS.Infrastructure;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class TrustedAuthoritySqliteSchemaContributionTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Rms_contribution_enforces_exact_v113_binding_and_append_only_evidence()
    {
        using var connection = OpenMemoryDatabase();
        Execute(connection, """
            CREATE TABLE RMS_RECIPE_EXECUTION_SNAPSHOT (
              EXECUTION_ID TEXT NOT NULL,
              WORK_SCOPE_ID TEXT NOT NULL,
              EQUIPMENT_ID TEXT NOT NULL,
              PROCESS_ID TEXT NULL,
              RECIPE_ID TEXT NOT NULL,
              RECIPE_VERSION INTEGER NOT NULL);
            CREATE TABLE RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE (
              EXECUTION_ID TEXT PRIMARY KEY,
              WORK_SCOPE_ID TEXT NOT NULL,
              PAIR_RUN_ID TEXT NOT NULL,
              SEQUENCE_RUN_ID TEXT NOT NULL,
              EQUIPMENT_ID TEXT NOT NULL,
              OPERATION_KEY TEXT NOT NULL,
              RECIPE_ID TEXT NOT NULL,
              RECIPE_VERSION INTEGER NOT NULL,
              SNAPSHOT_SCHEMA TEXT NOT NULL,
              SNAPSHOT_HASH TEXT NOT NULL,
              CAPTURED_AT TEXT NOT NULL);
            INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
              (EXECUTION_ID, WORK_SCOPE_ID, EQUIPMENT_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION)
            VALUES ('EXEC-1', 'WS-1', 'EQ-1', 'CLEANING', 'RCP-1', 2),
                   ('EXEC-2', 'WS-2', 'EQ-1', 'POLISH', 'RCP-1', 2),
                   ('EXEC-SPACE ', 'WS-1', 'EQ-1', 'CLEANING', 'RCP-1', 2);
            """);

        Apply(connection, new RmsTrustedAuthoritySqliteSchemaContribution());

        Execute(connection, CanonicalEvidenceInsert("EXEC-1"));
        var caseMismatch = () => Execute(connection, CanonicalEvidenceInsert("exec-1"));
        var processMismatch = () => Execute(connection, $"""
            INSERT INTO RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
              (EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID, EQUIPMENT_ID,
               OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH, CAPTURED_AT)
            VALUES ('EXEC-2', 'WS-2', 'PAIR-2', 'SEQ-2', 'EQ-1', 'CLEANING', 'RCP-1', 2,
                    'cleaner-recipe/v1', '{HashA}', '2026-08-31T00:00:00Z');
            """);
        var overlengthSchema = () => Execute(connection, $"""
            INSERT INTO RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
              (EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID, EQUIPMENT_ID,
               OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH, CAPTURED_AT)
            VALUES ('EXEC-2', 'WS-2', 'PAIR-2', 'SEQ-2', 'EQ-1', 'POLISH', 'RCP-1', 2,
                    '{new string('S', 101)}', '{HashA}', '2026-08-31T00:00:00Z');
            """);
        var boundarySpace = () => Execute(connection, CanonicalEvidenceInsert("EXEC-SPACE "));
        var update = () => Execute(connection, """
            UPDATE RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
               SET SNAPSHOT_SCHEMA = 'cleaner-recipe/v2'
             WHERE EXECUTION_ID = 'EXEC-1';
            """);

        caseMismatch.Should().Throw<SqliteException>()
            .WithMessage("*exact V113 execution*");
        processMismatch.Should().Throw<SqliteException>()
            .WithMessage("*exact V113 execution*");
        overlengthSchema.Should().Throw<SqliteException>()
            .WithMessage("*exact V113 execution*");
        boundarySpace.Should().Throw<SqliteException>()
            .WithMessage("*exact V113 execution*");
        update.Should().Throw<SqliteException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public void Rms_contribution_blocks_insert_or_replace_with_recursive_triggers_disabled()
    {
        using var connection = OpenMemoryDatabase();
        Execute(connection, "PRAGMA recursive_triggers=OFF;");
        Execute(connection, """
            CREATE TABLE RMS_RECIPE_EXECUTION_SNAPSHOT (
              EXECUTION_ID TEXT NOT NULL,
              WORK_SCOPE_ID TEXT NOT NULL,
              EQUIPMENT_ID TEXT NOT NULL,
              PROCESS_ID TEXT NULL,
              RECIPE_ID TEXT NOT NULL,
              RECIPE_VERSION INTEGER NOT NULL);
            CREATE TABLE RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE (
              EXECUTION_ID TEXT PRIMARY KEY,
              WORK_SCOPE_ID TEXT NOT NULL,
              PAIR_RUN_ID TEXT NOT NULL,
              SEQUENCE_RUN_ID TEXT NOT NULL,
              EQUIPMENT_ID TEXT NOT NULL,
              OPERATION_KEY TEXT NOT NULL,
              RECIPE_ID TEXT NOT NULL,
              RECIPE_VERSION INTEGER NOT NULL,
              SNAPSHOT_SCHEMA TEXT NOT NULL,
              SNAPSHOT_HASH TEXT NOT NULL,
              CAPTURED_AT TEXT NOT NULL);
            CREATE UNIQUE INDEX UX_RMS_CANONICAL_RECIPE_EXECUTION_STREAM
              ON RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
                 (WORK_SCOPE_ID COLLATE BINARY, PAIR_RUN_ID COLLATE BINARY,
                  SEQUENCE_RUN_ID COLLATE BINARY);
            INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
              (EXECUTION_ID, WORK_SCOPE_ID, EQUIPMENT_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION)
            VALUES ('EXEC-1', 'WS-1', 'EQ-1', 'CLEANING', 'RCP-1', 2),
                   ('EXEC-2', 'WS-1', 'EQ-1', 'CLEANING', 'RCP-1', 2);
            """);
        Apply(connection, new RmsTrustedAuthoritySqliteSchemaContribution());
        Execute(connection, CanonicalEvidenceInsert("EXEC-1"));

        var replaceSameExecution = () => Execute(connection,
            CanonicalEvidenceInsert("EXEC-1").Replace("INSERT INTO", "INSERT OR REPLACE INTO"));
        var replaceSameStream = () => Execute(connection,
            CanonicalEvidenceInsert("EXEC-2").Replace("INSERT INTO", "INSERT OR REPLACE INTO"));

        replaceSameExecution.Should().Throw<SqliteException>()
            .WithMessage("*replacement is forbidden*");
        replaceSameStream.Should().Throw<SqliteException>()
            .WithMessage("*replacement is forbidden*");
        Scalar(connection, "SELECT COUNT(*) FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE;")
            .Should().Be(1);
    }

    [Fact]
    public void Sys_contribution_makes_release_coordinate_exact_unique_and_revocation_append_only()
    {
        using var connection = OpenMemoryDatabase();
        CreateSysTables(connection);
        Apply(connection, new SysTrustedAuthoritySqliteSchemaContribution());

        Execute(connection, ProgramArtifactInsert("ART-1", "EQ-1", HashA));

        var sameCoordinateDifferentHash = () =>
            Execute(connection, ProgramArtifactInsert("ART-2", "EQ-1", HashB));
        sameCoordinateDifferentHash.Should().Throw<SqliteException>()
            .WithMessage("*coordinate already has immutable content*");

        Execute(connection, ProgramArtifactInsert("ART-3", "eq-1", HashB));
        Scalar(connection, "SELECT COUNT(*) FROM SYS_RELEASED_PROGRAM_ARTIFACT;")
            .Should().Be(2, "release coordinates use exact BINARY identity");

        var boundarySpace = () => Execute(
            connection,
            ProgramArtifactInsert("ART-SPACE ", "EQ-SPACE", HashB));
        boundarySpace.Should().Throw<SqliteException>()
            .WithMessage("*identities and hashes are invalid*");

        var blankReleaseActor = () => Execute(
            connection,
            ProgramArtifactInsert("ART-BLANK-ACTOR", "EQ-BLANK-ACTOR", HashB)
                .Replace("'release');", "'   ');"));
        blankReleaseActor.Should().Throw<SqliteException>()
            .WithMessage("*identities and hashes are invalid*");
        var overlengthReleaseActor = () => Execute(
            connection,
            ProgramArtifactInsert("ART-LONG-ACTOR", "EQ-LONG-ACTOR", HashB)
                .Replace("'release');", $"'{new string('R', 51)}');"));
        overlengthReleaseActor.Should().Throw<SqliteException>()
            .WithMessage("*identities and hashes are invalid*");

        var replace = () => Execute(connection, ProgramArtifactInsert(
            "ART-4",
            "EQ-1",
            HashB,
            "INSERT OR REPLACE"));
        replace.Should().Throw<SqliteException>()
            .WithMessage("*coordinate already has immutable content*");

        var mismatchedParent = () => Execute(connection, """
            INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
              (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
            VALUES ('REV-LOWER', 'art-1', '2026-08-31T00:00:00Z', 'operator', 'mismatch');
            """);
        mismatchedParent.Should().Throw<SqliteException>()
            .WithMessage("*requires a released artifact*");

        var blankRevocationReason = () => Execute(connection, """
            INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
              (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
            VALUES ('REV-BLANK', 'ART-1', '2026-08-31T00:00:00Z', 'operator', '   ');
            """);
        blankRevocationReason.Should().Throw<SqliteException>()
            .WithMessage("*provenance cannot be blank*");
        var overlengthRevocationReason = () => Execute(connection, $"""
            INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
              (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
            VALUES ('REV-LONG', 'ART-1', '2026-08-31T00:00:00Z', 'operator',
                    '{new string('R', 1001)}');
            """);
        overlengthRevocationReason.Should().Throw<SqliteException>()
            .WithMessage("*provenance cannot be blank*");

        Execute(connection, """
            INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
              (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
            VALUES ('REV-1', 'ART-1', '2026-08-31T00:00:00Z', 'operator', 'rollback');
            """);
        var revokeUpdate = () => Execute(connection, """
            UPDATE SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
               SET REASON = 'changed'
             WHERE REVOCATION_ID = 'REV-1';
            """);
        revokeUpdate.Should().Throw<SqliteException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public void Sys_contribution_blocks_identity_replacement_with_recursive_triggers_disabled()
    {
        using var connection = OpenMemoryDatabase();
        Execute(connection, "PRAGMA recursive_triggers=OFF;");
        CreateSysTables(connection);
        Apply(connection, new SysTrustedAuthoritySqliteSchemaContribution());
        Execute(connection, ProgramArtifactInsert("ART-1", "EQ-1", HashA));
        Execute(connection, ProgramArtifactInsert("ART-2", "EQ-2", HashB));
        Execute(connection, """
            INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
              (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
            VALUES ('REV-1', 'ART-1', '2026-08-31T00:00:00Z', 'operator', 'rollback');
            """);

        var replaceArtifactIdentity = () => Execute(connection, ProgramArtifactInsert(
            "ART-1", "EQ-3", HashB, "INSERT OR REPLACE"));
        var replaceRevocationIdentity = () => Execute(connection, """
            INSERT OR REPLACE INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
              (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
            VALUES ('REV-1', 'ART-2', '2026-08-31T00:01:00Z', 'operator', 'changed parent');
            """);

        replaceArtifactIdentity.Should().Throw<SqliteException>()
            .WithMessage("*artifact replacement is forbidden*");
        replaceRevocationIdentity.Should().Throw<SqliteException>()
            .WithMessage("*revocation replacement is forbidden*");
        Scalar(connection, "SELECT COUNT(*) FROM SYS_RELEASED_PROGRAM_ARTIFACT;")
            .Should().Be(2);
        Scalar(connection, "SELECT COUNT(*) FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION;")
            .Should().Be(1);
    }

    [Fact]
    public void Sys_contribution_fails_closed_when_legacy_release_coordinates_are_duplicated()
    {
        using var connection = OpenMemoryDatabase();
        CreateSysTables(connection);
        Execute(connection, ProgramArtifactInsert("ART-1", "EQ-1", HashA));
        Execute(connection, ProgramArtifactInsert("ART-2", "EQ-1", HashB));

        using var transaction = connection.BeginTransaction();
        var apply = () => new SysTrustedAuthoritySqliteSchemaContribution()
            .Apply(connection, transaction);

        apply.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate exact release coordinate*");
    }

    [Fact]
    public void Contributions_fail_closed_on_existing_malformed_rows_before_reinstalling_guards()
    {
        using (var rms = OpenMemoryDatabase())
        {
            Execute(rms, $"""
                CREATE TABLE RMS_RECIPE_EXECUTION_SNAPSHOT (
                  EXECUTION_ID TEXT NOT NULL, WORK_SCOPE_ID TEXT NOT NULL,
                  EQUIPMENT_ID TEXT NOT NULL, PROCESS_ID TEXT NULL,
                  RECIPE_ID TEXT NOT NULL, RECIPE_VERSION INTEGER NOT NULL);
                CREATE TABLE RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE (
                  EXECUTION_ID TEXT PRIMARY KEY, WORK_SCOPE_ID TEXT NOT NULL,
                  PAIR_RUN_ID TEXT NOT NULL, SEQUENCE_RUN_ID TEXT NOT NULL,
                  EQUIPMENT_ID TEXT NOT NULL, OPERATION_KEY TEXT NOT NULL,
                  RECIPE_ID TEXT NOT NULL, RECIPE_VERSION INTEGER NOT NULL,
                  SNAPSHOT_SCHEMA TEXT NOT NULL, SNAPSHOT_HASH TEXT NOT NULL,
                  CAPTURED_AT TEXT NOT NULL);
                INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
                  (EXECUTION_ID, WORK_SCOPE_ID, EQUIPMENT_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION)
                VALUES ('EXEC-LEGACY', 'WS-1', 'EQ-1', 'CLEANING', 'RCP-1', 2);
                INSERT INTO RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
                  (EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID, EQUIPMENT_ID,
                   OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH, CAPTURED_AT)
                VALUES ('EXEC-LEGACY', 'WS-1', 'PAIR-1', 'SEQ-1', 'EQ-1', 'CLEANING',
                        'RCP-1', 2, '{new string('S', 101)}', '{HashA}', '2026-08-31T00:00:00Z');
                """);
            using var transaction = rms.BeginTransaction();
            var apply = () => new RmsTrustedAuthoritySqliteSchemaContribution()
                .Apply(rms, transaction);
            apply.Should().Throw<InvalidOperationException>()
                .WithMessage("*invalid existing canonical execution*EXEC-LEGACY*");
        }

        using (var sys = OpenMemoryDatabase())
        {
            CreateSysTables(sys);
            Execute(sys, ProgramArtifactInsert("ART-LEGACY", "EQ-1", HashA)
                .Replace("'release');", "'   ');"));
            using var transaction = sys.BeginTransaction();
            var apply = () => new SysTrustedAuthoritySqliteSchemaContribution()
                .Apply(sys, transaction);
            apply.Should().Throw<InvalidOperationException>()
                .WithMessage("*invalid existing evidence*ART-LEGACY*");
        }

        using (var sys = OpenMemoryDatabase())
        {
            CreateSysTables(sys);
            Execute(sys, ProgramArtifactInsert("ART-1", "EQ-1", HashA));
            Execute(sys, """
                INSERT INTO SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
                  (REVOCATION_ID, ARTIFACT_ID, REVOKED_AT, REVOKED_BY, REASON)
                VALUES ('REV-ORPHAN', 'ART-MISSING', '2026-08-31T00:00:00Z', 'operator', 'legacy');
                """);
            using var transaction = sys.BeginTransaction();
            var apply = () => new SysTrustedAuthoritySqliteSchemaContribution()
                .Apply(sys, transaction);
            apply.Should().Throw<InvalidOperationException>()
                .WithMessage("*invalid existing evidence*REV-ORPHAN*");
        }
    }

    private static SqliteConnection OpenMemoryDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Apply(
        SqliteConnection connection,
        NexaOne.Infrastructure.Persistence.ISqliteSchemaContribution contribution)
    {
        using var transaction = connection.BeginTransaction();
        contribution.Apply(connection, transaction);
        transaction.Commit();
    }

    private static void CreateSysTables(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE SYS_RELEASED_PROGRAM_ARTIFACT (
          ARTIFACT_ID TEXT PRIMARY KEY,
          EQUIPMENT_ID TEXT NOT NULL,
          OPERATION_KEY TEXT NOT NULL,
          PRODUCT_PROFILE_ID TEXT NOT NULL,
          PLUGIN_ID TEXT NOT NULL,
          PRODUCT_DEFINITION_VERSION TEXT NOT NULL,
          PROGRAM_VERSION TEXT NOT NULL,
          PROGRAM_SCHEMA TEXT NOT NULL,
          PROGRAM_HASH TEXT NOT NULL,
          BOUND_RECIPE_SNAPSHOT_SCHEMA TEXT NOT NULL,
          BOUND_RECIPE_SNAPSHOT_HASH TEXT NOT NULL,
          RELEASED_AT TEXT NOT NULL,
          RELEASED_BY TEXT NOT NULL);
        CREATE TABLE SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION (
          REVOCATION_ID TEXT PRIMARY KEY,
          ARTIFACT_ID TEXT NOT NULL,
          REVOKED_AT TEXT NOT NULL,
          REVOKED_BY TEXT NOT NULL,
          REASON TEXT NOT NULL);
        """);

    private static string CanonicalEvidenceInsert(string executionId) => $"""
        INSERT INTO RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
          (EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID, EQUIPMENT_ID,
           OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH, CAPTURED_AT)
        VALUES ('{executionId}', 'WS-1', 'PAIR-1', 'SEQ-1', 'EQ-1', 'CLEANING', 'RCP-1', 2,
                'cleaner-recipe/v1', '{HashA}', '2026-08-31T00:00:00Z');
        """;

    private static string ProgramArtifactInsert(
        string artifactId,
        string equipmentId,
        string programHash,
        string insertVerb = "INSERT") => $"""
        {insertVerb} INTO SYS_RELEASED_PROGRAM_ARTIFACT
          (ARTIFACT_ID, EQUIPMENT_ID, OPERATION_KEY, PRODUCT_PROFILE_ID, PLUGIN_ID,
           PRODUCT_DEFINITION_VERSION, PROGRAM_VERSION, PROGRAM_SCHEMA, PROGRAM_HASH,
           BOUND_RECIPE_SNAPSHOT_SCHEMA, BOUND_RECIPE_SNAPSHOT_HASH, RELEASED_AT, RELEASED_BY)
        VALUES ('{artifactId}', '{equipmentId}', 'CLEANING', 'cleaner', 'plugin.cleaner',
                'product-v1', 'program-v1', 'cleaner-program/v2', '{programHash}',
                'cleaner-recipe/v1', '{HashA}', '2026-08-31T00:00:00Z', 'release');
        """;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
