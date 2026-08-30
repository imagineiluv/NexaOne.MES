using System.Data.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// Owns SQLite-only trigger reconciliation for the POM work-scope projection inbox and cursor.
/// The portable tables and indexes remain defined by V156; this contribution supplies the
/// SQLite equivalents of the SQL Server guards omitted by the migration translator.
/// </summary>
public sealed class PomWorkScopeProjectionSqliteSchemaContribution : ISqliteSchemaContribution
{
    public string Id => "POM.WorkScopeProjection.V156";

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
}
