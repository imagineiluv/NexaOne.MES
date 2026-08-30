using System.Data.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// Owns SQLite-only trigger reconciliation for the POM work-scope projection inbox, cursor, and
/// project-policy application queue. The portable tables and indexes remain defined by V156/V157;
/// this contribution supplies the SQLite equivalents of the SQL Server guards omitted by the
/// migration translator and performs V157's deterministic current-event-only upgrade backfill.
/// V158 also installs the explicit authority guards used to separate transport cursors from
/// projection-owned WorkScopes.
/// </summary>
public sealed class PomWorkScopeProjectionSqliteSchemaContribution : ISqliteSchemaContribution
{
    public string Id => "POM.WorkScopeProjection.V158";

    public void Apply(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_INBOX")) return;

        var triggerNames = new[]
        {
            "TR_POM_WORK_SCOPE_PROJECTION_INBOX_UPDATE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_INBOX_DELETE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_INBOX_REPLACE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_INBOX_SCOPE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_SCOPE_DELETE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_SCOPE_ID_UPDATE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_SCOPE_REPLACE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CURRENT_IDENTITY_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CURRENT_MONOTONIC_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT_BI",
            "TR_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT_BU",
            "TR_POM_WORK_SCOPE_PROJECTION_CURRENT_DELETE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CURRENT_REPLACE_GUARD",
        };
        foreach (var triggerName in triggerNames)
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");

        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_INBOX_UPDATE_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_INBOX
            BEGIN
              SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_INBOX is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_INBOX_DELETE_GUARD
            BEFORE DELETE ON POM_WORK_SCOPE_PROJECTION_INBOX
            BEGIN
              SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_INBOX is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_INBOX_REPLACE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_INBOX
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_INBOX E
               WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                 AND E.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_INBOX replacement is forbidden');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_INBOX_SCOPE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_INBOX
            WHEN NOT EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE S
               WHERE S.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                 AND S.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection requires exact equipment ownership');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_SCOPE_DELETE_GUARD
            BEFORE DELETE ON POM_WORK_SCOPE
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_INBOX E
               WHERE E.WORK_SCOPE_ID COLLATE BINARY = OLD.WORK_SCOPE_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM work scope is referenced by projection evidence');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_SCOPE_ID_UPDATE_GUARD
            BEFORE UPDATE OF WORK_SCOPE_ID ON POM_WORK_SCOPE
            WHEN OLD.WORK_SCOPE_ID COLLATE BINARY <> NEW.WORK_SCOPE_ID COLLATE BINARY
             AND EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_INBOX E
               WHERE E.WORK_SCOPE_ID COLLATE BINARY = OLD.WORK_SCOPE_ID COLLATE BINARY
                  OR E.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM work scope identity is referenced by projection evidence');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_SCOPE_REPLACE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE S
               WHERE S.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY)
             AND EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_INBOX E
               WHERE E.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM work scope replacement is forbidden while projection evidence exists');
            END;
            """);

        if (!HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_CURRENT")) return;

        ApplyCurrentWorkScopeBindingSchema(connection, transaction);

        const string eventConsistencyPredicate = """
            EXISTS (
              SELECT 1
                FROM POM_WORK_SCOPE_PROJECTION_INBOX E
               WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                 AND E.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY
                 AND E.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                 AND E.SEQUENCE_RUN_ID COLLATE BINARY = NEW.SEQUENCE_RUN_ID COLLATE BINARY
                 AND E.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                 AND E.OPERATION_KEY COLLATE BINARY = NEW.OPERATION_KEY COLLATE BINARY
                 AND E.PAIR_RUN_ID COLLATE BINARY = NEW.PAIR_RUN_ID COLLATE BINARY
                 AND E.RECIPE_ID COLLATE BINARY = NEW.RECIPE_ID COLLATE BINARY
                 AND E.RECIPE_SNAPSHOT_HASH COLLATE BINARY = NEW.RECIPE_SNAPSHOT_HASH COLLATE BINARY
                 AND E.PROGRAM_HASH COLLATE BINARY = NEW.PROGRAM_HASH COLLATE BINARY
                 AND E.CARRIERS_JSON COLLATE BINARY = NEW.CARRIERS_JSON COLLATE BINARY
                 AND E.SOURCE_REVISION = NEW.SOURCE_REVISION
                 AND E.PROJECTION_STATUS = NEW.PROJECTION_STATUS
                 AND E.TERMINAL_CLEANUP_COMPLETED = NEW.TERMINAL_CLEANUP_COMPLETED
                 AND E.OCCURRED_AT = NEW.OCCURRED_AT
                 AND E.ACCEPTED_AT = NEW.ACCEPTED_AT
                 AND E.ACCEPTED_AT = NEW.UPDATED_AT)
            """;
        Execute(connection, transaction, $"""
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT_BI
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_CURRENT
            WHEN NOT {eventConsistencyPredicate}
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection current cursor must reference its exact inbox event');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_REPLACE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_CURRENT
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_CURRENT C
               WHERE C.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                 AND C.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                 AND C.SEQUENCE_RUN_ID COLLATE BINARY = NEW.SEQUENCE_RUN_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection current cursor replacement is forbidden');
            END;
            """);
        Execute(connection, transaction, $"""
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_EVENT_BU
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_CURRENT
            WHEN NOT {eventConsistencyPredicate}
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection current cursor must reference its exact inbox event');
            END;
            """);
        // SQLite executes same-timing triggers in reverse creation order. Create the broad event
        // guard first so identity and monotonic violations retain the same precedence as MSSQL.
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_MONOTONIC_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_CURRENT
            WHEN NEW.SOURCE_REVISION < OLD.SOURCE_REVISION
              OR NEW.ACCEPTED_AT <= OLD.ACCEPTED_AT
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection current cursor must advance monotonically');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_IDENTITY_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_CURRENT
            WHEN OLD.SOURCE_CLIENT_ID COLLATE BINARY <> NEW.SOURCE_CLIENT_ID COLLATE BINARY
              OR OLD.EQUIPMENT_ID COLLATE BINARY <> NEW.EQUIPMENT_ID COLLATE BINARY
              OR OLD.SEQUENCE_RUN_ID COLLATE BINARY <> NEW.SEQUENCE_RUN_ID COLLATE BINARY
              OR OLD.WORK_SCOPE_ID COLLATE BINARY <> NEW.WORK_SCOPE_ID COLLATE BINARY
              OR OLD.OPERATION_KEY COLLATE BINARY <> NEW.OPERATION_KEY COLLATE BINARY
              OR OLD.PAIR_RUN_ID COLLATE BINARY <> NEW.PAIR_RUN_ID COLLATE BINARY
              OR OLD.RECIPE_ID COLLATE BINARY <> NEW.RECIPE_ID COLLATE BINARY
              OR OLD.RECIPE_SNAPSHOT_HASH COLLATE BINARY <> NEW.RECIPE_SNAPSHOT_HASH COLLATE BINARY
              OR OLD.PROGRAM_HASH COLLATE BINARY <> NEW.PROGRAM_HASH COLLATE BINARY
              OR OLD.CARRIERS_JSON COLLATE BINARY <> NEW.CARRIERS_JSON COLLATE BINARY
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection sequence identity is immutable');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CURRENT_DELETE_GUARD
            BEFORE DELETE ON POM_WORK_SCOPE_PROJECTION_CURRENT
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection current cursor is not deletable');
            END;
            """);

        ApplyAuthoritySchema(connection, transaction);
        ApplyApplicationSchema(connection, transaction);
        ApplyCarrierEvidenceSchema(connection, transaction);
    }

    private static void ApplyAuthoritySchema(
        DbConnection connection,
        DbTransaction transaction)
    {
        if (!HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_AUTHORITY")) return;

        Execute(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_POM_WORK_SCOPE_PROJECTION_AUTHORITY_STREAM
              ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
                 (SOURCE_CLIENT_ID COLLATE BINARY,
                  EQUIPMENT_ID COLLATE BINARY,
                  SEQUENCE_RUN_ID COLLATE BINARY);
            """);
        Execute(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_POM_WORK_SCOPE_PROJECTION_AUTHORITY_RECIPE_EXECUTION
              ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
                 (RECIPE_EXECUTION_ID COLLATE BINARY);
            """);

        var triggerNames = new[]
        {
            "TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_REPLACE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_SCOPE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_IDENTITY_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_DELETE_GUARD",
        };
        foreach (var triggerName in triggerNames)
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");

        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_REPLACE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY A
               WHERE A.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                  OR A.PROVISION_IDEMPOTENCY_KEY COLLATE BINARY
                       = NEW.PROVISION_IDEMPOTENCY_KEY COLLATE BINARY
                  OR A.RECIPE_EXECUTION_ID COLLATE BINARY
                       = NEW.RECIPE_EXECUTION_ID COLLATE BINARY
                  OR (A.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                      AND A.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                      AND A.SEQUENCE_RUN_ID COLLATE BINARY = NEW.SEQUENCE_RUN_ID COLLATE BINARY))
            BEGIN
              SELECT RAISE(ABORT, 'POM projection authority replacement is forbidden');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_SCOPE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
            WHEN NEW.RECIPE_SNAPSHOT_HASH GLOB '*[^0-9A-F]*'
              OR NEW.PROGRAM_HASH GLOB '*[^0-9A-F]*'
              OR NEW.PROVISION_REQUEST_HASH GLOB '*[^0-9A-F]*'
              OR NOT EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE S
               WHERE S.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                 AND S.STATUS = 'Created'
                 AND S.IS_HOLD = 'N'
                 AND S.VERSION_NO = 1
                 AND S.START_QTY = 0
                 AND S.COMPLETE_QTY = 0
                 AND S.SCRAP_QTY = 0
                 AND S.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                 AND S.TARGET_ID COLLATE BINARY = NEW.PAIR_RUN_ID COLLATE BINARY
                 AND S.RECIPE_ID COLLATE BINARY = NEW.RECIPE_ID COLLATE BINARY
                 AND S.RECIPE_VERSION = NEW.RECIPE_VERSION
                 AND NEW.BASELINE_VERSION_NO = S.VERSION_NO
                 AND NEW.LAST_APPLIED_VERSION_NO = S.VERSION_NO
                 AND NOT EXISTS (
                   SELECT 1 FROM POM_WORK_SCOPE_EXECUTION E
                    WHERE E.WORK_SCOPE_ID COLLATE BINARY = S.WORK_SCOPE_ID COLLATE BINARY))
            BEGIN
              SELECT RAISE(ABORT, 'POM projection authority requires an exact pristine WorkScope');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_IDENTITY_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
            WHEN OLD.WORK_SCOPE_ID COLLATE BINARY <> NEW.WORK_SCOPE_ID COLLATE BINARY
              OR OLD.SOURCE_CLIENT_ID COLLATE BINARY <> NEW.SOURCE_CLIENT_ID COLLATE BINARY
              OR OLD.EQUIPMENT_ID COLLATE BINARY <> NEW.EQUIPMENT_ID COLLATE BINARY
              OR OLD.OPERATION_KEY COLLATE BINARY <> NEW.OPERATION_KEY COLLATE BINARY
              OR OLD.PAIR_RUN_ID COLLATE BINARY <> NEW.PAIR_RUN_ID COLLATE BINARY
              OR OLD.SEQUENCE_RUN_ID COLLATE BINARY <> NEW.SEQUENCE_RUN_ID COLLATE BINARY
              OR OLD.RECIPE_EXECUTION_ID COLLATE BINARY <> NEW.RECIPE_EXECUTION_ID COLLATE BINARY
              OR OLD.RECIPE_ID COLLATE BINARY <> NEW.RECIPE_ID COLLATE BINARY
              OR OLD.RECIPE_VERSION <> NEW.RECIPE_VERSION
              OR OLD.RECIPE_SNAPSHOT_SCHEMA COLLATE BINARY <> NEW.RECIPE_SNAPSHOT_SCHEMA COLLATE BINARY
              OR OLD.RECIPE_SNAPSHOT_HASH COLLATE BINARY <> NEW.RECIPE_SNAPSHOT_HASH COLLATE BINARY
              OR OLD.PROGRAM_ARTIFACT_ID COLLATE BINARY <> NEW.PROGRAM_ARTIFACT_ID COLLATE BINARY
              OR OLD.PROGRAM_SCHEMA COLLATE BINARY <> NEW.PROGRAM_SCHEMA COLLATE BINARY
              OR OLD.PROGRAM_HASH COLLATE BINARY <> NEW.PROGRAM_HASH COLLATE BINARY
              OR OLD.BASELINE_VERSION_NO <> NEW.BASELINE_VERSION_NO
              OR OLD.PROVISION_IDEMPOTENCY_KEY COLLATE BINARY
                   <> NEW.PROVISION_IDEMPOTENCY_KEY COLLATE BINARY
              OR OLD.PROVISION_REQUEST_HASH COLLATE BINARY
                   <> NEW.PROVISION_REQUEST_HASH COLLATE BINARY
              OR OLD.PROVISIONED_AT <> NEW.PROVISIONED_AT
              OR OLD.PROVISIONED_BY COLLATE BINARY <> NEW.PROVISIONED_BY COLLATE BINARY
            BEGIN
              SELECT RAISE(ABORT, 'POM projection authority identity is immutable');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_LINEAGE_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
            WHEN NEW.LAST_APPLIED_VERSION_NO < OLD.LAST_APPLIED_VERSION_NO
              OR (NEW.LAST_APPLIED_VERSION_NO = OLD.LAST_APPLIED_VERSION_NO
                  AND NEW.LAST_APPLIED_AT IS NOT OLD.LAST_APPLIED_AT)
              OR (NEW.LAST_APPLIED_VERSION_NO > OLD.LAST_APPLIED_VERSION_NO
                  AND (NOT EXISTS (
                         SELECT 1 FROM POM_WORK_SCOPE S
                          WHERE S.WORK_SCOPE_ID COLLATE BINARY
                                  = NEW.WORK_SCOPE_ID COLLATE BINARY
                            AND S.VERSION_NO = NEW.LAST_APPLIED_VERSION_NO)
                       OR (NEW.LAST_APPLIED_VERSION_NO > NEW.BASELINE_VERSION_NO
                           AND NEW.LAST_APPLIED_AT IS NULL)
                       OR (OLD.LAST_APPLIED_AT IS NOT NULL
                           AND (NEW.LAST_APPLIED_AT IS NULL
                                OR NEW.LAST_APPLIED_AT < OLD.LAST_APPLIED_AT))))
            BEGIN
              SELECT RAISE(ABORT, 'POM projection authority applied lineage is monotonic and scope-aligned');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_AUTHORITY_DELETE_GUARD
            BEFORE DELETE ON POM_WORK_SCOPE_PROJECTION_AUTHORITY
            BEGIN
              SELECT RAISE(ABORT, 'POM projection authority is not deletable');
            END;
            """);
    }

    private static void ApplyCurrentWorkScopeBindingSchema(
        DbConnection connection,
        DbTransaction transaction)
    {
        var duplicate = FindViolation(connection, transaction, """
            SELECT WORK_SCOPE_ID
              FROM POM_WORK_SCOPE_PROJECTION_CURRENT
             GROUP BY WORK_SCOPE_ID COLLATE BINARY
            HAVING COUNT(*) > 1
             LIMIT 1;
            """);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"V157 projection current contains duplicate WorkScope bindings: {duplicate}");
        }

        Execute(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_POM_WORK_SCOPE_PROJECTION_CURRENT_WORK_SCOPE
              ON POM_WORK_SCOPE_PROJECTION_CURRENT (WORK_SCOPE_ID COLLATE BINARY);
            """);
    }

    private static void ApplyCarrierEvidenceSchema(
        DbConnection connection,
        DbTransaction transaction)
    {
        if (!HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_CARRIER")) return;

        var invalidSource = FindViolation(connection, transaction, """
            SELECT E.SOURCE_CLIENT_ID || ':' || E.EVENT_ID
              FROM POM_WORK_SCOPE_PROJECTION_INBOX E
             WHERE CASE
                 WHEN json_valid(E.CARRIERS_JSON) = 0 THEN 1
                 WHEN json_type(E.CARRIERS_JSON) <> 'array' THEN 1
                 WHEN json_array_length(E.CARRIERS_JSON) <> 2 THEN 1
                 WHEN EXISTS (
                    SELECT 1
                      FROM json_each(E.CARRIERS_JSON) J
                     WHERE J.type <> 'object'
                        OR json_type(J.value, '$.lane') IS NOT 'text'
                        OR length(trim(json_extract(J.value, '$.lane'))) NOT BETWEEN 1 AND 30
                        OR length(json_extract(J.value, '$.lane')) > 30
                        OR json_type(J.value, '$.carrierId') IS NOT 'text'
                        OR length(trim(json_extract(J.value, '$.carrierId'))) NOT BETWEEN 1 AND 100
                        OR length(json_extract(J.value, '$.carrierId')) > 100
                        OR json_type(J.value, '$.cleaningRunId') IS NOT 'text'
                        OR length(trim(json_extract(J.value, '$.cleaningRunId'))) NOT BETWEEN 1 AND 100
                        OR length(json_extract(J.value, '$.cleaningRunId')) > 100)
                     THEN 1
                 WHEN (SELECT COUNT(DISTINCT json_extract(J.value, '$.lane') COLLATE BINARY)
                         FROM json_each(E.CARRIERS_JSON) J) <> 2 THEN 1
                 WHEN (SELECT COUNT(DISTINCT json_extract(J.value, '$.carrierId') COLLATE BINARY)
                         FROM json_each(E.CARRIERS_JSON) J) <> 2 THEN 1
                 ELSE 0
             END = 1
             LIMIT 1;
            """);
        if (invalidSource is not null)
        {
            throw new InvalidOperationException(
                $"V157 projection carrier source is not an exact two-carrier array: {invalidSource}");
        }

        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_POM_WORK_SCOPE_PROJECTION_CARRIER_ID
              ON POM_WORK_SCOPE_PROJECTION_CARRIER
                 (CARRIER_ID, ACCEPTED_AT DESC, SOURCE_CLIENT_ID, EVENT_ID);
            """);
        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_POM_WORK_SCOPE_PROJECTION_CLEANING_RUN
              ON POM_WORK_SCOPE_PROJECTION_CARRIER
                 (CLEANING_RUN_ID, ACCEPTED_AT DESC, SOURCE_CLIENT_ID, EVENT_ID);
            """);

        var triggerNames = new[]
        {
            "TR_POM_WORK_SCOPE_PROJECTION_CARRIER_REPLACE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CARRIER_INBOX_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CARRIER_UPDATE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_CARRIER_DELETE_GUARD",
        };
        foreach (var triggerName in triggerNames)
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");

        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_REPLACE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_CARRIER
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_CARRIER C
               WHERE C.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                 AND C.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY
                 AND (C.CARRIER_ID COLLATE BINARY = NEW.CARRIER_ID COLLATE BINARY
                      OR C.LANE COLLATE BINARY = NEW.LANE COLLATE BINARY))
            BEGIN
              SELECT RAISE(ABORT, 'POM projection carrier replacement is forbidden');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_INBOX_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_CARRIER
            WHEN length(trim(NEW.LANE)) NOT BETWEEN 1 AND 30
              OR length(NEW.LANE) > 30
              OR length(trim(NEW.CARRIER_ID)) NOT BETWEEN 1 AND 100
              OR length(NEW.CARRIER_ID) > 100
              OR length(trim(NEW.CLEANING_RUN_ID)) NOT BETWEEN 1 AND 100
              OR length(NEW.CLEANING_RUN_ID) > 100
              OR NOT EXISTS (
                SELECT 1
                  FROM POM_WORK_SCOPE_PROJECTION_INBOX E,
                       json_each(E.CARRIERS_JSON) J
                 WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                   AND E.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY
                   AND E.ACCEPTED_AT = NEW.ACCEPTED_AT
                   AND json_extract(J.value, '$.carrierId') COLLATE BINARY
                         = NEW.CARRIER_ID COLLATE BINARY
                   AND json_extract(J.value, '$.lane') COLLATE BINARY
                         = NEW.LANE COLLATE BINARY
                   AND json_extract(J.value, '$.cleaningRunId') COLLATE BINARY
                         = NEW.CLEANING_RUN_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM projection carrier must reference its exact inbox evidence');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_UPDATE_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_CARRIER
            BEGIN
              SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_CARRIER is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_CARRIER_DELETE_GUARD
            BEFORE DELETE ON POM_WORK_SCOPE_PROJECTION_CARRIER
            BEGIN
              SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_CARRIER is append-only');
            END;
            """);

        var invalidEvidence = FindViolation(connection, transaction, """
            SELECT C.SOURCE_CLIENT_ID || ':' || C.EVENT_ID || ':' || C.CARRIER_ID
              FROM POM_WORK_SCOPE_PROJECTION_CARRIER C
             WHERE NOT EXISTS (
                SELECT 1
                  FROM POM_WORK_SCOPE_PROJECTION_INBOX E,
                       json_each(E.CARRIERS_JSON) J
                 WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = C.SOURCE_CLIENT_ID COLLATE BINARY
                   AND E.EVENT_ID COLLATE BINARY = C.EVENT_ID COLLATE BINARY
                   AND E.ACCEPTED_AT = C.ACCEPTED_AT
                   AND json_extract(J.value, '$.carrierId') COLLATE BINARY
                         = C.CARRIER_ID COLLATE BINARY
                   AND json_extract(J.value, '$.lane') COLLATE BINARY = C.LANE COLLATE BINARY
                   AND json_extract(J.value, '$.cleaningRunId') COLLATE BINARY
                         = C.CLEANING_RUN_ID COLLATE BINARY)
             LIMIT 1;
            """);
        if (invalidEvidence is not null)
        {
            throw new InvalidOperationException(
                $"V157 projection carrier row does not match immutable inbox evidence: {invalidEvidence}");
        }

        Execute(connection, transaction, """
            INSERT INTO POM_WORK_SCOPE_PROJECTION_CARRIER
                (SOURCE_CLIENT_ID, EVENT_ID, CARRIER_ID, LANE, CLEANING_RUN_ID, ACCEPTED_AT)
            SELECT E.SOURCE_CLIENT_ID, E.EVENT_ID,
                   json_extract(J.value, '$.carrierId'),
                   json_extract(J.value, '$.lane'),
                   json_extract(J.value, '$.cleaningRunId'),
                   E.ACCEPTED_AT
              FROM POM_WORK_SCOPE_PROJECTION_INBOX E,
                   json_each(E.CARRIERS_JSON) J
             WHERE NOT EXISTS (
                SELECT 1
                  FROM POM_WORK_SCOPE_PROJECTION_CARRIER C
                 WHERE C.SOURCE_CLIENT_ID COLLATE BINARY = E.SOURCE_CLIENT_ID COLLATE BINARY
                   AND C.EVENT_ID COLLATE BINARY = E.EVENT_ID COLLATE BINARY
                   AND C.CARRIER_ID COLLATE BINARY
                         = json_extract(J.value, '$.carrierId') COLLATE BINARY);
            """);

        var incompleteEvidence = FindViolation(connection, transaction, """
            SELECT E.SOURCE_CLIENT_ID || ':' || E.EVENT_ID
              FROM POM_WORK_SCOPE_PROJECTION_INBOX E
             WHERE (SELECT COUNT(*)
                      FROM POM_WORK_SCOPE_PROJECTION_CARRIER C
                     WHERE C.SOURCE_CLIENT_ID COLLATE BINARY = E.SOURCE_CLIENT_ID COLLATE BINARY
                       AND C.EVENT_ID COLLATE BINARY = E.EVENT_ID COLLATE BINARY) <> 2
             LIMIT 1;
            """);
        if (incompleteEvidence is not null)
        {
            throw new InvalidOperationException(
                $"V157 projection carrier evidence is incomplete after reconciliation: {incompleteEvidence}");
        }
    }

    private static void ApplyApplicationSchema(
        DbConnection connection,
        DbTransaction transaction)
    {
        if (!HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_APPLICATION")) return;

        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_READY
              ON POM_WORK_SCOPE_PROJECTION_APPLICATION
                 (APPLICATION_STATUS, NEXT_ATTEMPT_AT, ACCEPTED_AT,
                  SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID, SOURCE_REVISION, EVENT_ID);
            """);
        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_STREAM
              ON POM_WORK_SCOPE_PROJECTION_APPLICATION
                 (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
                  SOURCE_REVISION, ACCEPTED_AT, EVENT_ID);
            """);
        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS IX_POM_WORK_SCOPE_PROJECTION_STREAM_ORDER
              ON POM_WORK_SCOPE_PROJECTION_INBOX
                 (SOURCE_CLIENT_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
                  SOURCE_REVISION, ACCEPTED_AT, EVENT_ID);
            """);

        var applicationTriggerNames = new[]
        {
            "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_REPLACE_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_INBOX_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_IDENTITY_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_MONOTONIC_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_TERMINAL_GUARD",
            "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_DELETE_GUARD",
        };
        foreach (var triggerName in applicationTriggerNames)
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");

        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_REPLACE_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_APPLICATION
            WHEN EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
               WHERE A.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                 AND A.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'POM projection application replacement is forbidden');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_INBOX_GUARD
            BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_APPLICATION
            WHEN NOT EXISTS (
              SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_INBOX E
               WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                 AND E.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY
                 AND E.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                 AND E.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                 AND E.SEQUENCE_RUN_ID COLLATE BINARY = NEW.SEQUENCE_RUN_ID COLLATE BINARY
                 AND E.SOURCE_REVISION = NEW.SOURCE_REVISION
                 AND E.ACCEPTED_AT = NEW.ACCEPTED_AT)
            BEGIN
              SELECT RAISE(ABORT, 'POM projection application must reference its exact inbox event');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_IDENTITY_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_APPLICATION
            WHEN OLD.SOURCE_CLIENT_ID COLLATE BINARY <> NEW.SOURCE_CLIENT_ID COLLATE BINARY
              OR OLD.EVENT_ID COLLATE BINARY <> NEW.EVENT_ID COLLATE BINARY
              OR OLD.WORK_SCOPE_ID COLLATE BINARY <> NEW.WORK_SCOPE_ID COLLATE BINARY
              OR OLD.EQUIPMENT_ID COLLATE BINARY <> NEW.EQUIPMENT_ID COLLATE BINARY
              OR OLD.SEQUENCE_RUN_ID COLLATE BINARY <> NEW.SEQUENCE_RUN_ID COLLATE BINARY
              OR OLD.SOURCE_REVISION <> NEW.SOURCE_REVISION
              OR OLD.ACCEPTED_AT <> NEW.ACCEPTED_AT
              OR OLD.CREATED_BY COLLATE BINARY <> NEW.CREATED_BY COLLATE BINARY
              OR OLD.CREATED_AT <> NEW.CREATED_AT
            BEGIN
              SELECT RAISE(ABORT, 'POM projection application identity is immutable');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_MONOTONIC_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_APPLICATION
            WHEN NEW.ATTEMPT_COUNT < OLD.ATTEMPT_COUNT
              OR NEW.LEASE_FENCE < OLD.LEASE_FENCE
            BEGIN
              SELECT RAISE(ABORT, 'POM projection application attempts and lease fence are monotonic');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_TERMINAL_GUARD
            BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_APPLICATION
            WHEN OLD.APPLICATION_STATUS IN ('Applied', 'Observed', 'Superseded', 'Quarantined')
             AND (NEW.APPLICATION_STATUS <> OLD.APPLICATION_STATUS
               OR NEW.ATTEMPT_COUNT <> OLD.ATTEMPT_COUNT
               OR NEW.NEXT_ATTEMPT_AT IS NOT OLD.NEXT_ATTEMPT_AT
               OR NEW.LEASE_OWNER IS NOT OLD.LEASE_OWNER
               OR NEW.LEASE_FENCE <> OLD.LEASE_FENCE
               OR NEW.LEASE_EXPIRES_AT IS NOT OLD.LEASE_EXPIRES_AT
               OR NEW.POLICY_ID IS NOT OLD.POLICY_ID
               OR NEW.POLICY_REVISION IS NOT OLD.POLICY_REVISION
               OR NEW.DECISION_HASH IS NOT OLD.DECISION_HASH
               OR NEW.DECISION_JSON IS NOT OLD.DECISION_JSON
               OR NEW.LAST_ERROR_CODE IS NOT OLD.LAST_ERROR_CODE
               OR NEW.LAST_ERROR_MESSAGE IS NOT OLD.LAST_ERROR_MESSAGE
               OR NEW.COMPLETED_AT IS NOT OLD.COMPLETED_AT)
            BEGIN
              SELECT RAISE(ABORT, 'POM projection application terminal state cannot regress or mutate');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_DELETE_GUARD
            BEFORE DELETE ON POM_WORK_SCOPE_PROJECTION_APPLICATION
            BEGIN
              SELECT RAISE(ABORT, 'POM work-scope projection application is not deletable or replaceable');
            END;
            """);

        if (HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT"))
        {
            Execute(connection, transaction, """
                CREATE INDEX IF NOT EXISTS IX_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT
                  ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                     (SOURCE_CLIENT_ID, EVENT_ID, OCCURRED_AT, APPLICATION_EVENT_ID);
                """);

            var eventTriggerNames = new[]
            {
                "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT_GUARD",
                "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_REPLACE_GUARD",
                "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_UPDATE_GUARD",
                "TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_DELETE_GUARD",
            };
            foreach (var triggerName in eventTriggerNames)
                Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");

            Execute(connection, transaction, """
                CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_PARENT_GUARD
                BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                WHEN NOT EXISTS (
                  SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
                   WHERE A.SOURCE_CLIENT_ID COLLATE BINARY = NEW.SOURCE_CLIENT_ID COLLATE BINARY
                     AND A.EVENT_ID COLLATE BINARY = NEW.EVENT_ID COLLATE BINARY)
                BEGIN
                  SELECT RAISE(ABORT, 'POM projection application event requires its application parent');
                END;
                """);
            Execute(connection, transaction, """
                CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_REPLACE_GUARD
                BEFORE INSERT ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                WHEN EXISTS (
                  SELECT 1 FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT E
                   WHERE E.APPLICATION_EVENT_ID COLLATE BINARY
                         = NEW.APPLICATION_EVENT_ID COLLATE BINARY)
                BEGIN
                  SELECT RAISE(ABORT, 'POM projection application event replacement is forbidden');
                END;
                """);
            Execute(connection, transaction, """
                CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_UPDATE_GUARD
                BEFORE UPDATE ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                BEGIN
                  SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT is append-only');
                END;
                """);
            Execute(connection, transaction, """
                CREATE TRIGGER TR_POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT_DELETE_GUARD
                BEFORE DELETE ON POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                BEGIN
                  SELECT RAISE(ABORT, 'POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT is append-only');
                END;
                """);
        }

        // SQLite's generic incremental migration pass intentionally skips DML. Reconcile only the
        // exact inbox events selected by V156 CURRENT; historical non-current evidence is never
        // replayed merely because V157 was installed. NOT EXISTS keeps every restart idempotent while
        // allowing constraint/identity corruption to surface instead of being hidden by OR IGNORE.
        Execute(connection, transaction, """
            INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION
                (SOURCE_CLIENT_ID, EVENT_ID, WORK_SCOPE_ID, EQUIPMENT_ID, SEQUENCE_RUN_ID,
                 SOURCE_REVISION, ACCEPTED_AT, APPLICATION_STATUS, ATTEMPT_COUNT,
                 NEXT_ATTEMPT_AT, LEASE_OWNER, LEASE_FENCE, LEASE_EXPIRES_AT,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT E.SOURCE_CLIENT_ID, E.EVENT_ID, E.WORK_SCOPE_ID, E.EQUIPMENT_ID,
                   E.SEQUENCE_RUN_ID, E.SOURCE_REVISION, E.ACCEPTED_AT, 'Pending', 0,
                   NULL, NULL, 0, NULL,
                   'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP
              FROM POM_WORK_SCOPE_PROJECTION_CURRENT C
              JOIN POM_WORK_SCOPE_PROJECTION_INBOX E
                ON E.SOURCE_CLIENT_ID COLLATE BINARY = C.SOURCE_CLIENT_ID COLLATE BINARY
               AND E.EVENT_ID COLLATE BINARY = C.EVENT_ID COLLATE BINARY
               AND E.WORK_SCOPE_ID COLLATE BINARY = C.WORK_SCOPE_ID COLLATE BINARY
               AND E.EQUIPMENT_ID COLLATE BINARY = C.EQUIPMENT_ID COLLATE BINARY
               AND E.SEQUENCE_RUN_ID COLLATE BINARY = C.SEQUENCE_RUN_ID COLLATE BINARY
               AND E.SOURCE_REVISION = C.SOURCE_REVISION
               AND E.ACCEPTED_AT = C.ACCEPTED_AT
             WHERE NOT EXISTS (
                SELECT 1
                  FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
                 WHERE A.SOURCE_CLIENT_ID COLLATE BINARY = E.SOURCE_CLIENT_ID COLLATE BINARY
                   AND A.EVENT_ID COLLATE BINARY = E.EVENT_ID COLLATE BINARY);
            """);

        EnsureInitialApplicationAudits(connection, transaction);
    }

    private static void EnsureInitialApplicationAudits(
        DbConnection connection,
        DbTransaction transaction)
    {
        if (!HasTable(connection, transaction, "POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT")) return;

        var seeds = new List<(string SourceClientId, string EventId, object CreatedAt)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT A.SOURCE_CLIENT_ID, A.EVENT_ID, A.CREATED_AT
                  FROM POM_WORK_SCOPE_PROJECTION_APPLICATION A
                 WHERE NOT EXISTS (
                    SELECT 1
                      FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT E
                     WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = A.SOURCE_CLIENT_ID COLLATE BINARY
                       AND E.EVENT_ID COLLATE BINARY = A.EVENT_ID COLLATE BINARY
                       AND E.EVENT_TYPE = 'Pending'
                       AND E.TO_STATUS = 'Pending'
                       AND E.ATTEMPT_COUNT = 0
                       AND E.LEASE_FENCE = 0);
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
                seeds.Add((reader.GetString(0), reader.GetString(1), reader.GetValue(2)));
        }

        foreach (var seed in seeds)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT
                    (APPLICATION_EVENT_ID, SOURCE_CLIENT_ID, EVENT_ID, EVENT_TYPE,
                     FROM_STATUS, TO_STATUS, ATTEMPT_COUNT, LEASE_FENCE,
                     POLICY_ID, POLICY_REVISION, DECISION_HASH, DECISION_JSON,
                     ERROR_CODE, ERROR_MESSAGE, OCCURRED_AT, CREATED_BY, CREATED_AT)
                SELECT @applicationEventId, @sourceClientId, @eventId, 'Pending',
                       NULL, 'Pending', 0, 0,
                       NULL, NULL, NULL, NULL,
                       NULL, NULL, @createdAt, 'SYSTEM', @createdAt
                 WHERE NOT EXISTS (
                    SELECT 1
                      FROM POM_WORK_SCOPE_PROJECTION_APPLICATION_EVENT E
                     WHERE E.SOURCE_CLIENT_ID COLLATE BINARY = @sourceClientId COLLATE BINARY
                       AND E.EVENT_ID COLLATE BINARY = @eventId COLLATE BINARY
                       AND E.EVENT_TYPE = 'Pending'
                       AND E.TO_STATUS = 'Pending'
                       AND E.ATTEMPT_COUNT = 0
                       AND E.LEASE_FENCE = 0);
                """;
            AddParameter(insert, "@applicationEventId", ProjectionIdentity.Audit(
                seed.SourceClientId, seed.EventId, "Pending", 0, 0));
            AddParameter(insert, "@sourceClientId", seed.SourceClientId);
            AddParameter(insert, "@eventId", seed.EventId);
            AddParameter(insert, "@createdAt", seed.CreatedAt);
            insert.ExecuteNonQuery();
        }
    }

    private static bool HasTable(
        DbConnection connection,
        DbTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
              FROM sqlite_master
             WHERE type = 'table'
               AND name = @tableName;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void Execute(
        DbConnection connection,
        DbTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? FindViolation(
        DbConnection connection,
        DbTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
