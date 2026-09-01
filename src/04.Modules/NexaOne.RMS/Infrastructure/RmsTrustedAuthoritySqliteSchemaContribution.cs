using System.Data.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.RMS.Infrastructure;

/// <summary>
/// Owns the SQLite equivalents of V159's RMS canonical execution evidence guards. The portable
/// table and indexes remain migration-owned; this contribution restores append-only and exact
/// V113 execution binding after every SQLite schema reconciliation.
/// </summary>
public sealed class RmsTrustedAuthoritySqliteSchemaContribution : ISqliteSchemaContribution
{
    public string Id => "RMS.TrustedAuthority.V159";

    public void Apply(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!HasTable(connection, transaction, "RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE")) return;
        if (!HasTable(connection, transaction, "RMS_RECIPE_EXECUTION_SNAPSHOT"))
        {
            throw new InvalidOperationException(
                "V159 trusted RMS evidence requires the V113 recipe execution snapshot table.");
        }

        var invalidExecutionId = FindInvalidEvidence(connection, transaction);
        if (invalidExecutionId is not null)
        {
            throw new InvalidOperationException(
                "V159 trusted RMS evidence contains an invalid existing canonical execution: "
                + invalidExecutionId);
        }

        foreach (var triggerName in new[]
        {
            "TR_RMS_CANONICAL_RECIPE_EXECUTION_APPEND_ONLY_BU",
            "TR_RMS_CANONICAL_RECIPE_EXECUTION_APPEND_ONLY_BD",
            "TR_RMS_CANONICAL_RECIPE_EXECUTION_PRISTINE_BI",
            "TR_RMS_CANONICAL_RECIPE_EXECUTION_REPLACE_BI",
        })
        {
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");
        }

        Execute(connection, transaction, """
            CREATE TRIGGER TR_RMS_CANONICAL_RECIPE_EXECUTION_APPEND_ONLY_BU
            BEFORE UPDATE ON RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
            BEGIN
              SELECT RAISE(ABORT, 'Trusted RMS canonical execution evidence is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_RMS_CANONICAL_RECIPE_EXECUTION_APPEND_ONLY_BD
            BEFORE DELETE ON RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
            BEGIN
              SELECT RAISE(ABORT, 'Trusted RMS canonical execution evidence is append-only');
            END;
            """);
        // UPDATE/DELETE triggers do not protect INSERT OR REPLACE when recursive_triggers is off.
        // Guard both immutable identities before SQLite can delete the conflicting evidence row.
        // Create this before the pristine guard because SQLite runs same-timing triggers in reverse
        // creation order and malformed evidence must keep the validation-error precedence.
        Execute(connection, transaction, """
            CREATE TRIGGER TR_RMS_CANONICAL_RECIPE_EXECUTION_REPLACE_BI
            BEFORE INSERT ON RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
            WHEN EXISTS (
              SELECT 1
                FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE E
               WHERE E.EXECUTION_ID COLLATE BINARY = NEW.EXECUTION_ID COLLATE BINARY
                  OR (E.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                      AND E.PAIR_RUN_ID COLLATE BINARY = NEW.PAIR_RUN_ID COLLATE BINARY
                      AND E.SEQUENCE_RUN_ID COLLATE BINARY = NEW.SEQUENCE_RUN_ID COLLATE BINARY))
            BEGIN
              SELECT RAISE(ABORT, 'Trusted RMS canonical execution evidence replacement is forbidden');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_RMS_CANONICAL_RECIPE_EXECUTION_PRISTINE_BI
            BEFORE INSERT ON RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE
            WHEN LENGTH(NEW.SNAPSHOT_HASH) <> 64
              OR NEW.SNAPSHOT_HASH GLOB '*[^0-9A-F]*'
              OR LENGTH(NEW.EXECUTION_ID) > 100
              OR LENGTH(NEW.WORK_SCOPE_ID) > 50
              OR LENGTH(NEW.PAIR_RUN_ID) > 100
              OR LENGTH(NEW.SEQUENCE_RUN_ID) > 100
              OR LENGTH(NEW.EQUIPMENT_ID) > 100
              OR LENGTH(NEW.OPERATION_KEY) > 200
              OR LENGTH(NEW.RECIPE_ID) > 100
              OR LENGTH(NEW.SNAPSHOT_SCHEMA) > 100
              OR LENGTH(TRIM(NEW.EXECUTION_ID)) = 0 OR NEW.EXECUTION_ID <> TRIM(NEW.EXECUTION_ID)
              OR LENGTH(TRIM(NEW.WORK_SCOPE_ID)) = 0 OR NEW.WORK_SCOPE_ID <> TRIM(NEW.WORK_SCOPE_ID)
              OR LENGTH(TRIM(NEW.PAIR_RUN_ID)) = 0 OR NEW.PAIR_RUN_ID <> TRIM(NEW.PAIR_RUN_ID)
              OR LENGTH(TRIM(NEW.SEQUENCE_RUN_ID)) = 0 OR NEW.SEQUENCE_RUN_ID <> TRIM(NEW.SEQUENCE_RUN_ID)
              OR LENGTH(TRIM(NEW.EQUIPMENT_ID)) = 0 OR NEW.EQUIPMENT_ID <> TRIM(NEW.EQUIPMENT_ID)
              OR LENGTH(TRIM(NEW.OPERATION_KEY)) = 0 OR NEW.OPERATION_KEY <> TRIM(NEW.OPERATION_KEY)
              OR LENGTH(TRIM(NEW.RECIPE_ID)) = 0 OR NEW.RECIPE_ID <> TRIM(NEW.RECIPE_ID)
              OR LENGTH(TRIM(NEW.SNAPSHOT_SCHEMA)) = 0 OR NEW.SNAPSHOT_SCHEMA <> TRIM(NEW.SNAPSHOT_SCHEMA)
              OR NOT EXISTS (
                SELECT 1
                  FROM RMS_RECIPE_EXECUTION_SNAPSHOT S
                 WHERE S.EXECUTION_ID COLLATE BINARY = NEW.EXECUTION_ID COLLATE BINARY
                   AND S.WORK_SCOPE_ID COLLATE BINARY = NEW.WORK_SCOPE_ID COLLATE BINARY
                   AND S.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                   AND S.PROCESS_ID IS NOT NULL
                   AND S.PROCESS_ID COLLATE BINARY = NEW.OPERATION_KEY COLLATE BINARY
                   AND S.RECIPE_ID COLLATE BINARY = NEW.RECIPE_ID COLLATE BINARY
                   AND S.RECIPE_VERSION = NEW.RECIPE_VERSION)
            BEGIN
              SELECT RAISE(ABORT, 'Canonical recipe evidence must bind an exact V113 execution');
            END;
            """);
    }

    private static string? FindInvalidEvidence(
        DbConnection connection,
        DbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT E.EXECUTION_ID
              FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE E
             WHERE LENGTH(E.SNAPSHOT_HASH) <> 64
                OR E.SNAPSHOT_HASH GLOB '*[^0-9A-F]*'
                OR E.RECIPE_VERSION <= 0
                OR LENGTH(E.EXECUTION_ID) > 100
                OR LENGTH(E.WORK_SCOPE_ID) > 50
                OR LENGTH(E.PAIR_RUN_ID) > 100
                OR LENGTH(E.SEQUENCE_RUN_ID) > 100
                OR LENGTH(E.EQUIPMENT_ID) > 100
                OR LENGTH(E.OPERATION_KEY) > 200
                OR LENGTH(E.RECIPE_ID) > 100
                OR LENGTH(E.SNAPSHOT_SCHEMA) > 100
                OR LENGTH(TRIM(E.EXECUTION_ID)) = 0 OR E.EXECUTION_ID <> TRIM(E.EXECUTION_ID)
                OR LENGTH(TRIM(E.WORK_SCOPE_ID)) = 0 OR E.WORK_SCOPE_ID <> TRIM(E.WORK_SCOPE_ID)
                OR LENGTH(TRIM(E.PAIR_RUN_ID)) = 0 OR E.PAIR_RUN_ID <> TRIM(E.PAIR_RUN_ID)
                OR LENGTH(TRIM(E.SEQUENCE_RUN_ID)) = 0 OR E.SEQUENCE_RUN_ID <> TRIM(E.SEQUENCE_RUN_ID)
                OR LENGTH(TRIM(E.EQUIPMENT_ID)) = 0 OR E.EQUIPMENT_ID <> TRIM(E.EQUIPMENT_ID)
                OR LENGTH(TRIM(E.OPERATION_KEY)) = 0 OR E.OPERATION_KEY <> TRIM(E.OPERATION_KEY)
                OR LENGTH(TRIM(E.RECIPE_ID)) = 0 OR E.RECIPE_ID <> TRIM(E.RECIPE_ID)
                OR LENGTH(TRIM(E.SNAPSHOT_SCHEMA)) = 0 OR E.SNAPSHOT_SCHEMA <> TRIM(E.SNAPSHOT_SCHEMA)
                OR NOT EXISTS (
                    SELECT 1
                      FROM RMS_RECIPE_EXECUTION_SNAPSHOT S
                     WHERE S.EXECUTION_ID COLLATE BINARY = E.EXECUTION_ID COLLATE BINARY
                       AND S.WORK_SCOPE_ID COLLATE BINARY = E.WORK_SCOPE_ID COLLATE BINARY
                       AND S.EQUIPMENT_ID COLLATE BINARY = E.EQUIPMENT_ID COLLATE BINARY
                       AND S.PROCESS_ID IS NOT NULL
                       AND S.PROCESS_ID COLLATE BINARY = E.OPERATION_KEY COLLATE BINARY
                       AND S.RECIPE_ID COLLATE BINARY = E.RECIPE_ID COLLATE BINARY
                       AND S.RECIPE_VERSION = E.RECIPE_VERSION)
             LIMIT 1;
            """;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static bool HasTable(
        DbConnection connection,
        DbTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name COLLATE NOCASE;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
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
