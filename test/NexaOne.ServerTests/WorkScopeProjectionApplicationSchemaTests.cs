using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>V157 project-policy application queue의 portable schema와 직접 쓰기 방지 계약입니다.</summary>
public sealed class WorkScopeProjectionApplicationSchemaTests
{
    [Fact]
    public void V157_migration_defines_current_only_backfill_ordering_and_durable_guards()
    {
        var sql = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V157__POM_WORK_SCOPE_PROJECTION_APPLICATION.sql"));

        sql.Should().StartWith("-- Owner: POM.");
        sql.Should().Contain("CREATE TABLE POM_WORK_SCOPE_PROJECTION_APPLICATION (");
        sql.Should().Contain("PRIMARY KEY (SOURCE_CLIENT_ID, EVENT_ID)");
        sql.Should().Contain("REFERENCES POM_WORK_SCOPE_PROJECTION_INBOX (SOURCE_CLIENT_ID, EVENT_ID)");
        sql.Should().Contain("'Pending', 'Processing', 'Retry', 'Applied', 'Observed', 'Superseded', 'Quarantined'");
        sql.Should().Contain("ATTEMPT_COUNT       INT");
        sql.Should().Contain("LEASE_FENCE         BIGINT");
        sql.Should().Contain("POLICY_ID           NVARCHAR(100)");
        sql.Should().Contain("POLICY_REVISION     NVARCHAR(50)");
        sql.Should().Contain("DECISION_HASH       CHAR(64) COLLATE Latin1_General_100_BIN2");
        sql.Should().Contain("DECISION_JSON       NVARCHAR(MAX)");
        sql.Should().Contain("IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_READY");
        sql.Should().Contain("IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_STREAM");
        sql.Should().Contain("IX_POM_WORK_SCOPE_PROJECTION_STREAM_ORDER");
        sql.Should().Contain("THROW 51541, 'POM work-scope projection current contains duplicate WorkScope bindings'");
        sql.Should().Contain("ALTER COLUMN WORK_SCOPE_ID NVARCHAR(50) COLLATE Latin1_General_100_BIN2 NOT NULL");
        sql.Should().Contain("UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE");
        sql.Should().Contain("SOURCE_REVISION, ACCEPTED_AT, EVENT_ID");
        sql.Should().Contain("CREATE TABLE POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT (");
        sql.Should().Contain("CREATE TABLE POM_WORK_SCOPE_PROJECTION_CARRIER (");
        sql.Should().Contain("PRIMARY KEY (SOURCE_CLIENT_ID, EVENT_ID, CARRIER_ID)");
        sql.Should().Contain("UNIQUE (SOURCE_CLIENT_ID, EVENT_ID, LANE)");
        sql.Should().Contain("IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID");
        sql.Should().Contain("IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN");
        sql.Should().Contain("CROSS APPLY OPENJSON(E.CARRIERS_JSON)");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_CARRIER_GUARD");
        sql.Should().Contain("projection carrier must reference its exact inbox evidence");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_GUARD");
        sql.Should().Contain("attempts and lease fence are monotonic");
        sql.Should().Contain("terminal state cannot regress or mutate");
        sql.Should().Contain("TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_APPEND_ONLY");
        sql.Should().Contain("AFTER UPDATE, DELETE");
        sql.Should().Contain("Latin1_General_100_BIN2_UTF8");
        sql.Should().Contain("xs:base64Binary(sql:column(\"D.DIGEST\"))");
        sql.Should().Contain("N'7:Pending1:01:0'");
        sql.Should().Contain("FROM POM_WORK_SCOPE_PROJECTION_CURRENT C");
        sql.Should().Contain("JOIN POM_WORK_SCOPE_PROJECTION_INBOX E");
        sql.Should().NotContain("FROM POM_WORK_SCOPE_PROJECTION_INBOX E\n+ WHERE NOT EXISTS",
            "the upgrade must not enqueue historical non-current evidence");
        sql.Should().Contain("-- SQLITE-OMIT-BEGIN");
        sql.Should().Contain("-- SQLITE-OMIT-END");
    }

    [Fact]
    public void V157_can_promote_current_work_scope_identity_to_bin2_before_indexing()
    {
        var v156 = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V156__POM_WORK_SCOPE_PROJECTION_INBOX.sql"));
        var currentStart = v156.IndexOf(
            "CREATE TABLE POM_WORK_SCOPE_PROJECTION_CURRENT (",
            StringComparison.Ordinal);
        var currentEnd = v156.IndexOf(
            "-- SQL Server keeps inbox evidence append-only.",
            currentStart,
            StringComparison.Ordinal);
        var currentDefinition = v156[currentStart..currentEnd];

        currentDefinition.Should().Contain("WORK_SCOPE_ID        NVARCHAR(50)  NOT NULL");
        currentDefinition.Should().NotContain("FOREIGN KEY (WORK_SCOPE_ID)",
            "ALTER COLUMN collation must not be blocked by a V156 foreign key");
        currentDefinition.Should().NotContain("UNIQUE (WORK_SCOPE_ID)",
            "V157 performs the duplicate preflight before adding uniqueness");
        v156.Should().NotContain(
            "ON POM_WORK_SCOPE_PROJECTION_CURRENT (WORK_SCOPE_ID",
            "V157 must be the first physical index over the column whose collation it changes");
    }

    [Fact]
    public void Projection_ingestion_locks_scope_before_reverse_current_binding_on_sql_server()
    {
        var source = File.ReadAllText(RepositorySource.GetFile(
            "src", "04.Modules", "NexaOne.POM", "Infrastructure",
            "WorkScopeProjectionRepository.cs"));
        var scopeRead = source.IndexOf(
            "var scope = await QueryFirstOrDefaultAsync<ScopeIdentityRow>",
            StringComparison.Ordinal);
        var reverseBindingRead = source.IndexOf(
            "var scopeBinding = await QueryFirstOrDefaultAsync<WorkScopeBindingRow>",
            StringComparison.Ordinal);

        scopeRead.Should().BeGreaterThan(-1);
        reverseBindingRead.Should().BeGreaterThan(scopeRead);
        source.Should().Contain("FROM POM_WORK_SCOPE WITH (UPDLOCK, HOLDLOCK)");
        source.Should().Contain("FROM POM_WORK_SCOPE_PROJECTION_CURRENT WITH (UPDLOCK, HOLDLOCK)");
        source.Should().Contain("WorkScopeProjectionPersistKind.WorkScopeBindingConflict");
    }

    [Fact]
    public void Sqlite_v157_backfills_only_current_event_once_and_restores_all_queue_artifacts()
    {
        using var database = ProjectionSchemaDatabase.Create();
        database.SeedTwoEventsAndCurrent();
        database.Execute("""
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_READY;
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_STREAM;
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_STREAM_ORDER;
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT;
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID;
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN;
            DROP INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE;
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_TERMINAL_GUARD;
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_DELETE_GUARD;
            """);

        database.EnsureSchema();
        database.EnsureSchema();

        database.TableExists("POM_WORK_SCOPE_PROJECTION_APPLICATION").Should().BeTrue();
        database.TableExists("POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_READY").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_STREAM").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_STREAM_ORDER").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN").Should().BeTrue();
        database.IndexExists("UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE").Should().BeTrue();
        database.Scalar("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION")
            .Should().Be(1);
        database.Text("""
            SELECT EVENT_ID || ':' || APPLICATION_STATUS || ':' || ATTEMPT_COUNT || ':' || LEASE_FENCE
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
            """).Should().Be("event-current:Pending:0:0");
        database.Scalar("""
            SELECT COUNT(*)
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
             WHERE NOT EXISTS (
                SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT E
                 WHERE E.SOURCE_CLIENT_ID = A.SOURCE_CLIENT_ID
                   AND E.EVENT_ID = A.EVENT_ID
                   AND E.EVENT_TYPE = 'Pending' AND E.TO_STATUS = 'Pending'
                   AND E.ATTEMPT_COUNT = 0 AND E.LEASE_FENCE = 0)
            """).Should().Be(0, "every application starts with one durable Pending audit");
        database.Text("""
            SELECT APPLICATION_EVENT_ID || ':' || EVENT_TYPE || ':' || TO_STATUS
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
            """).Should().Be($"{AuditId("cleaner-a", "event-current", "Pending", 0, 0)}:Pending:Pending");
        database.Scalar("""
            SELECT COUNT(*) FROM sqlite_master
             WHERE type='trigger'
               AND name LIKE 'TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_%'
            """).Should().Be(10);
        database.Scalar("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER")
            .Should().Be(4, "carrier evidence backfills every inbox event, not only CURRENT");
    }

    [Fact]
    public void Sqlite_v157_rejects_duplicate_work_scope_bindings_before_repairing_the_unique_index()
    {
        using var database = ProjectionSchemaDatabase.Create();
        database.SeedTwoEventsAndCurrent();
        database.EnsureSchema();
        database.Execute("""
            DROP INDEX UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE;

            INSERT INTO POM_WORK_SCOPE_PROJECTION_INBOX
                (SOURCE_CLIENT_ID, EVENT_ID, REQUEST_HASH, WORK_SCOPE_ID, EQUIPMENT_ID,
                 OPERATION_KEY, PAIR_RUN_ID, SEQUENCE_RUN_ID, SOURCE_REVISION,
                 PROJECTION_STATUS, TERMINAL_CLEANUP_COMPLETED, RECIPE_ID,
                 RECIPE_SNAPSHOT_HASH, PROGRAM_HASH, CARRIERS_JSON, RESULT_CODE,
                 RESULT_METADATA_JSON, OCCURRED_AT, PAYLOAD_JSON, ACCEPTED_AT,
                 CREATED_BY, CREATED_AT)
            SELECT SOURCE_CLIENT_ID, 'event-conflicting-current',
                   '3333333333333333333333333333333333333333333333333333333333333333',
                   WORK_SCOPE_ID, EQUIPMENT_ID, OPERATION_KEY, PAIR_RUN_ID, 'sequence-2', 1,
                   PROJECTION_STATUS, TERMINAL_CLEANUP_COMPLETED, RECIPE_ID,
                   RECIPE_SNAPSHOT_HASH, PROGRAM_HASH, CARRIERS_JSON, RESULT_CODE,
                   RESULT_METADATA_JSON, '2026-08-30T00:00:04.0000000Z', PAYLOAD_JSON,
                   '2026-08-30T00:00:05.0000000Z', CREATED_BY,
                   '2026-08-30T00:00:05.0000000Z'
              FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE EVENT_ID='event-current';

            INSERT INTO POM_WORK_SCOPE_PROJECTION_CURRENT
                (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, EVENT_ID, WORK_SCOPE_ID,
                 OPERATION_KEY, PAIR_RUN_ID, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH,
                 CARRIERS_JSON, SOURCE_REVISION, PROJECTION_STATUS,
                 TERMINAL_CLEANUP_COMPLETED, OCCURRED_AT, ACCEPTED_AT, UPDATED_AT)
            SELECT SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, EVENT_ID, WORK_SCOPE_ID,
                   OPERATION_KEY, PAIR_RUN_ID, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH,
                   CARRIERS_JSON, SOURCE_REVISION, PROJECTION_STATUS,
                   TERMINAL_CLEANUP_COMPLETED, OCCURRED_AT, ACCEPTED_AT, ACCEPTED_AT
              FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE EVENT_ID='event-conflicting-current';
            """);

        Action repair = database.EnsureSchema;

        repair.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate WorkScope bindings: WS-1*");
        database.IndexExists("UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE")
            .Should().BeFalse("ambiguous bindings must never be normalized silently");
    }

    [Fact]
    public void Sqlite_v157_repairs_all_carrier_history_idempotently_and_keeps_it_append_only()
    {
        using var database = ProjectionSchemaDatabase.Create();
        database.SeedTwoEventsAndCurrent();
        database.EnsureSchema();
        database.Execute("""
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID;
            DROP INDEX IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN;
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_REPLACE_GUARD;
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_INBOX_GUARD;
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_UPDATE_GUARD;
            DROP TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_DELETE_GUARD;
            DELETE FROM POM_WORK_SCOPE_PROJECTION_CARRIER
             WHERE EVENT_ID='event-history' AND CARRIER_ID='CARRIER-F';
            """);

        database.EnsureSchema();
        database.EnsureSchema();

        database.Scalar("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_CARRIER").Should().Be(4);
        database.Text("""
            SELECT GROUP_CONCAT(EVENT_ID || ':' || LANE || ':' || CARRIER_ID || ':' || CLEANING_RUN_ID, '|')
              FROM (
                SELECT EVENT_ID, LANE, CARRIER_ID, CLEANING_RUN_ID
                  FROM POM_WORK_SCOPE_PROJECTION_CARRIER
                 ORDER BY EVENT_ID, LANE)
            """).Should().Be(
                "event-current:front:CARRIER-F:RUN-F|event-current:rear:CARRIER-R:RUN-R|"
                + "event-history:front:CARRIER-F:RUN-F|event-history:rear:CARRIER-R:RUN-R");
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID").Should().BeTrue();
        database.IndexExists("IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN").Should().BeTrue();
        database.Scalar("""
            SELECT COUNT(*) FROM sqlite_master
             WHERE type='trigger' AND name LIKE 'TR_POM_WORK_SCOPE_PROJECTION_CARRIER_%'
            """).Should().Be(4);

        Action update = () => database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_CARRIER SET LANE='mutated'
             WHERE EVENT_ID='event-current' AND CARRIER_ID='CARRIER-F';
            """);
        Action delete = () => database.Execute("""
            DELETE FROM POM_WORK_SCOPE_PROJECTION_CARRIER
             WHERE EVENT_ID='event-current' AND CARRIER_ID='CARRIER-F';
            """);
        Action replace = () => database.Execute("""
            PRAGMA recursive_triggers=OFF;
            INSERT OR REPLACE INTO POM_WORK_SCOPE_PROJECTION_CARRIER
            SELECT * FROM POM_WORK_SCOPE_PROJECTION_CARRIER
             WHERE EVENT_ID='event-current' AND CARRIER_ID='CARRIER-F';
            """);
        Action fabricate = () => database.Execute("""
            INSERT INTO POM_WORK_SCOPE_PROJECTION_CARRIER
                (SOURCE_CLIENT_ID, EVENT_ID, CARRIER_ID, LANE, CLEANING_RUN_ID, ACCEPTED_AT)
            SELECT SOURCE_CLIENT_ID, EVENT_ID, 'CARRIER-X', 'side', 'RUN-X', ACCEPTED_AT
              FROM POM_WORK_SCOPE_PROJECTION_INBOX WHERE EVENT_ID='event-current';
            """);

        update.Should().Throw<SqliteException>().WithMessage("*append-only*");
        delete.Should().Throw<SqliteException>().WithMessage("*append-only*");
        replace.Should().Throw<SqliteException>().WithMessage("*replacement is forbidden*");
        fabricate.Should().Throw<SqliteException>().WithMessage("*exact inbox evidence*");
    }

    [Fact]
    public void Sqlite_v157_rejects_identity_rollback_terminal_regression_delete_replace_and_audit_mutation()
    {
        using var database = ProjectionSchemaDatabase.Create();
        database.SeedTwoEventsAndCurrent();
        database.EnsureSchema();

        Action mutateIdentity = () => database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET WORK_SCOPE_ID='WS-MUTATED'
             WHERE EVENT_ID='event-current';
            """);
        Action mutateCreatedAudit = () => database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET CREATED_BY='tampered'
             WHERE EVENT_ID='event-current';
            """);
        mutateIdentity.Should().Throw<SqliteException>().WithMessage("*identity is immutable*");
        mutateCreatedAudit.Should().Throw<SqliteException>().WithMessage("*identity is immutable*");

        database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET APPLICATION_STATUS='Processing', ATTEMPT_COUNT=1,
                   LEASE_OWNER='worker-a', LEASE_FENCE=1,
                   LEASE_EXPIRES_AT='2026-08-30T01:05:00.0000000Z',
                   UPDATED_AT='2026-08-30T01:00:00.0000000Z'
             WHERE EVENT_ID='event-current';
            """);

        database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET UPDATED_BY='repair-audit', UPDATED_AT='2026-08-30T01:03:00.0000000Z'
             WHERE EVENT_ID='event-current';
            """);
        database.Text("""
            SELECT UPDATED_BY || ':' || UPDATED_AT
              FROM POM_WORK_SCOPE_PROJECTION_APPLICATION
             WHERE EVENT_ID='event-current'
            """).Should().Be("repair-audit:2026-08-30T01:03:00.0000000Z",
                "terminal rows allow only operational touch metadata corrections");
        Action decreaseCounters = () => database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET ATTEMPT_COUNT=0, LEASE_FENCE=0
             WHERE EVENT_ID='event-current';
            """);
        decreaseCounters.Should().Throw<SqliteException>().WithMessage("*are monotonic*");

        database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET APPLICATION_STATUS='Retry', NEXT_ATTEMPT_AT='2026-08-30T01:10:00.0000000Z',
                   LEASE_OWNER=NULL, LEASE_EXPIRES_AT=NULL,
                   LAST_ERROR_CODE='POLICY_RETRY', LAST_ERROR_MESSAGE='retry requested',
                   UPDATED_AT='2026-08-30T01:01:00.0000000Z'
             WHERE EVENT_ID='event-current';
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET APPLICATION_STATUS='Quarantined', NEXT_ATTEMPT_AT=NULL,
                   POLICY_ID='project.cleaner', POLICY_REVISION='1',
                   DECISION_HASH='AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                   DECISION_JSON='{"disposition":"Quarantine"}',
                   COMPLETED_AT='2026-08-30T01:02:00.0000000Z',
                   UPDATED_AT='2026-08-30T01:02:00.0000000Z'
             WHERE EVENT_ID='event-current';
            """);

        Action regressTerminal = () => database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION
               SET APPLICATION_STATUS='Pending', POLICY_ID=NULL, POLICY_REVISION=NULL,
                   DECISION_HASH=NULL, DECISION_JSON=NULL, COMPLETED_AT=NULL
             WHERE EVENT_ID='event-current';
            """);
        Action deleteApplication = () => database.Execute(
            "DELETE FROM POM_WORK_SCOPE_PROJECTION_APPLICATION WHERE EVENT_ID='event-current';");
        Action replaceApplication = () => database.Execute("""
            PRAGMA recursive_triggers=OFF;
            INSERT OR REPLACE INTO POM_WORK_SCOPE_PROJECTION_APPLICATION
            SELECT * FROM POM_WORK_SCOPE_PROJECTION_APPLICATION WHERE EVENT_ID='event-current';
            """);

        regressTerminal.Should().Throw<SqliteException>()
            .WithMessage("*terminal state cannot regress or mutate*");
        deleteApplication.Should().Throw<SqliteException>()
            .WithMessage("*not deletable or replaceable*");
        replaceApplication.Should().Throw<SqliteException>()
            .WithMessage("*replacement is forbidden*");

        database.Execute("""
            INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                (APPLICATION_EVENT_ID, SOURCE_CLIENT_ID, EVENT_ID, EVENT_TYPE,
                 FROM_STATUS, TO_STATUS, ATTEMPT_COUNT, LEASE_FENCE,
                 POLICY_ID, POLICY_REVISION, DECISION_HASH, DECISION_JSON,
                 ERROR_CODE, ERROR_MESSAGE, OCCURRED_AT, CREATED_BY, CREATED_AT)
            VALUES
                ('pae_contract', 'cleaner-a', 'event-current', 'Quarantined',
                 'Retry', 'Quarantined', 1, 1,
                 'project.cleaner', '1',
                 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                 '{"disposition":"Quarantine"}', 'POLICY_RETRY', 'retry requested',
                 '2026-08-30T01:02:00.0000000Z', 'SYSTEM', '2026-08-30T01:02:00.0000000Z');
            """);

        Action mutateAudit = () => database.Execute("""
            UPDATE POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
               SET ERROR_MESSAGE='tampered'
             WHERE APPLICATION_EVENT_ID='pae_contract';
            """);
        Action deleteAudit = () => database.Execute("""
            DELETE FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE APPLICATION_EVENT_ID='pae_contract';
            """);
        Action replaceAudit = () => database.Execute("""
            PRAGMA recursive_triggers=OFF;
            INSERT OR REPLACE INTO POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
            SELECT * FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
             WHERE APPLICATION_EVENT_ID='pae_contract';
            """);

        mutateAudit.Should().Throw<SqliteException>().WithMessage("*append-only*");
        deleteAudit.Should().Throw<SqliteException>().WithMessage("*append-only*");
        replaceAudit.Should().Throw<SqliteException>().WithMessage("*replacement is forbidden*");
        database.Scalar("SELECT COUNT(*) FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT")
            .Should().Be(2, "the initial Pending audit and terminal audit are both immutable");
    }

    private static string AuditId(
        string sourceClientId,
        string eventId,
        string eventType,
        long leaseFence,
        int attemptCount)
    {
        var builder = new StringBuilder();
        foreach (var value in new[]
                 {
                     sourceClientId, eventId, eventType,
                     leaseFence.ToString(), attemptCount.ToString(),
                 })
            builder.Append(value.Length).Append(':').Append(value);
        var digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"pae_{digest}";
    }

    private sealed class ProjectionSchemaDatabase : IDisposable
    {
        private static readonly PomWorkScopeProjectionSqliteSchemaContribution[] Contributions =
            [new PomWorkScopeProjectionSqliteSchemaContribution()];

        private ProjectionSchemaDatabase(string path)
        {
            Path = path;
            ConnectionString = $"Data Source={path};Foreign Keys=False";
            SqliteSchemaInitializer.Apply(ConnectionString, Contributions);
        }

        private string Path { get; }
        private string ConnectionString { get; }

        public static ProjectionSchemaDatabase Create() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"nexa-projection-application-{Guid.NewGuid():N}.db"));

        public void EnsureSchema() =>
            SqliteSchemaInitializer.EnsureSchema(ConnectionString, Contributions);

        public void SeedTwoEventsAndCurrent() => Execute("""
            INSERT INTO POM_WORK_SCOPE
                (WORK_SCOPE_ID, PLANT_ID, SCOPE_TYPE, TARGET_ID, NAME, EQUIPMENT_ID,
                 CREATE_IDEMPOTENCY_KEY, CREATE_REQUEST_HASH)
            VALUES
                ('WS-1', 'PLANT-1', 'Equipment', 'EQ-1', 'Cleaner', 'EQ-1',
                 'test:create:WS-1',
                 'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC');

            INSERT INTO POM_WORK_SCOPE_PROJECTION_INBOX
                (SOURCE_CLIENT_ID, EVENT_ID, REQUEST_HASH, WORK_SCOPE_ID, EQUIPMENT_ID,
                 OPERATION_KEY, PAIR_RUN_ID, SEQUENCE_RUN_ID, SOURCE_REVISION,
                 PROJECTION_STATUS, TERMINAL_CLEANUP_COMPLETED, RECIPE_ID,
                 RECIPE_SNAPSHOT_HASH, PROGRAM_HASH, CARRIERS_JSON, RESULT_CODE,
                 RESULT_METADATA_JSON, OCCURRED_AT, PAYLOAD_JSON, ACCEPTED_AT,
                 CREATED_BY, CREATED_AT)
            VALUES
                ('cleaner-a', 'event-history',
                 '1111111111111111111111111111111111111111111111111111111111111111',
                 'WS-1', 'EQ-1', 'operation-1', 'pair-1', 'sequence-1', 1,
                 'Running', 0, 'RECIPE-1',
                 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
                 '[{"lane":"front","carrierId":"CARRIER-F","cleaningRunId":"RUN-F"},{"lane":"rear","carrierId":"CARRIER-R","cleaningRunId":"RUN-R"}]',
                 'PAIR_RUNNING', NULL,
                 '2026-08-30T00:00:00.0000000Z', '{}', '2026-08-30T00:00:01.0000000Z',
                 'SYSTEM', '2026-08-30T00:00:01.0000000Z'),
                ('cleaner-a', 'event-current',
                 '2222222222222222222222222222222222222222222222222222222222222222',
                 'WS-1', 'EQ-1', 'operation-1', 'pair-1', 'sequence-1', 2,
                 'Running', 0, 'RECIPE-1',
                 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
                 '[{"lane":"front","carrierId":"CARRIER-F","cleaningRunId":"RUN-F"},{"lane":"rear","carrierId":"CARRIER-R","cleaningRunId":"RUN-R"}]',
                 'PAIR_RUNNING', NULL,
                 '2026-08-30T00:00:02.0000000Z', '{}', '2026-08-30T00:00:03.0000000Z',
                 'SYSTEM', '2026-08-30T00:00:03.0000000Z');

            INSERT INTO POM_WORK_SCOPE_PROJECTION_CURRENT
                (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, EVENT_ID, WORK_SCOPE_ID,
                 OPERATION_KEY, PAIR_RUN_ID, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH,
                 CARRIERS_JSON, SOURCE_REVISION, PROJECTION_STATUS,
                 TERMINAL_CLEANUP_COMPLETED, OCCURRED_AT, ACCEPTED_AT, UPDATED_AT)
            SELECT SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, EVENT_ID, WORK_SCOPE_ID,
                   OPERATION_KEY, PAIR_RUN_ID, RECIPE_ID, RECIPE_SNAPSHOT_HASH, PROGRAM_HASH,
                   CARRIERS_JSON, SOURCE_REVISION, PROJECTION_STATUS,
                   TERMINAL_CLEANUP_COMPLETED, OCCURRED_AT, ACCEPTED_AT, ACCEPTED_AT
              FROM POM_WORK_SCOPE_PROJECTION_INBOX
             WHERE EVENT_ID='event-current';
            """);

        public bool TableExists(string name) => Scalar(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{name}'") > 0;

        public bool IndexExists(string name) => Scalar(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{name}'") > 0;

        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public string Text(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        }

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(Path); } catch { /* best-effort temporary database cleanup */ }
        }
    }
}
