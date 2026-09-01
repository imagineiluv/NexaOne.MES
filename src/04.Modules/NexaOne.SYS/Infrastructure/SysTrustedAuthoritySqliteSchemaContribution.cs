using System.Data.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.SYS.Infrastructure;

/// <summary>
/// Owns the SQLite equivalents of V159's released-program artifact and revocation guards. A release
/// coordinate is exact and immutable: one equipment/operation/profile/plugin/product/program/schema
/// tuple can identify only one released artifact, regardless of content hash.
/// </summary>
public sealed class SysTrustedAuthoritySqliteSchemaContribution : ISqliteSchemaContribution
{
    public string Id => "SYS.TrustedAuthority.V159";

    public void Apply(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!HasTable(connection, transaction, "SYS_RELEASED_PROGRAM_ARTIFACT")) return;

        var invalidArtifact = FindInvalidArtifact(connection, transaction);
        if (invalidArtifact is not null)
        {
            throw new InvalidOperationException(
                "V159 released program artifacts contain invalid existing evidence: "
                + invalidArtifact);
        }

        var duplicateCoordinate = FindDuplicateCoordinate(connection, transaction);
        if (duplicateCoordinate is not null)
        {
            throw new InvalidOperationException(
                "V159 released program artifacts contain a duplicate exact release coordinate: "
                + duplicateCoordinate);
        }

        Execute(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SYS_RELEASED_PROGRAM_ARTIFACT_COORDINATE
              ON SYS_RELEASED_PROGRAM_ARTIFACT
                 (EQUIPMENT_ID COLLATE BINARY,
                  OPERATION_KEY COLLATE BINARY,
                  PRODUCT_PROFILE_ID COLLATE BINARY,
                  PLUGIN_ID COLLATE BINARY,
                  PRODUCT_DEFINITION_VERSION COLLATE BINARY,
                  PROGRAM_VERSION COLLATE BINARY,
                  PROGRAM_SCHEMA COLLATE BINARY);
            """);

        foreach (var triggerName in new[]
        {
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_APPEND_ONLY_BU",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_APPEND_ONLY_BD",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_VALIDATE_BI",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_COORDINATE_BI",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_REPLACE_BI",
        })
        {
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");
        }

        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_APPEND_ONLY_BU
            BEFORE UPDATE ON SYS_RELEASED_PROGRAM_ARTIFACT
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact evidence is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_APPEND_ONLY_BD
            BEFORE DELETE ON SYS_RELEASED_PROGRAM_ARTIFACT
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact evidence is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_VALIDATE_BI
            BEFORE INSERT ON SYS_RELEASED_PROGRAM_ARTIFACT
            WHEN LENGTH(NEW.PROGRAM_HASH) <> 64
              OR NEW.PROGRAM_HASH GLOB '*[^0-9A-F]*'
              OR LENGTH(NEW.BOUND_RECIPE_SNAPSHOT_HASH) <> 64
              OR NEW.BOUND_RECIPE_SNAPSHOT_HASH GLOB '*[^0-9A-F]*'
              OR LENGTH(NEW.ARTIFACT_ID) > 200
              OR LENGTH(NEW.EQUIPMENT_ID) > 100
              OR LENGTH(NEW.OPERATION_KEY) > 200
              OR LENGTH(NEW.PRODUCT_PROFILE_ID) > 100
              OR LENGTH(NEW.PLUGIN_ID) > 200
              OR LENGTH(NEW.PRODUCT_DEFINITION_VERSION) > 100
              OR LENGTH(NEW.PROGRAM_VERSION) > 100
              OR LENGTH(NEW.PROGRAM_SCHEMA) > 100
              OR LENGTH(NEW.BOUND_RECIPE_SNAPSHOT_SCHEMA) > 100
              OR LENGTH(NEW.RELEASED_BY) > 50
              OR LENGTH(TRIM(NEW.ARTIFACT_ID)) = 0 OR NEW.ARTIFACT_ID <> TRIM(NEW.ARTIFACT_ID)
              OR LENGTH(TRIM(NEW.EQUIPMENT_ID)) = 0 OR NEW.EQUIPMENT_ID <> TRIM(NEW.EQUIPMENT_ID)
              OR LENGTH(TRIM(NEW.OPERATION_KEY)) = 0 OR NEW.OPERATION_KEY <> TRIM(NEW.OPERATION_KEY)
              OR LENGTH(TRIM(NEW.PRODUCT_PROFILE_ID)) = 0 OR NEW.PRODUCT_PROFILE_ID <> TRIM(NEW.PRODUCT_PROFILE_ID)
              OR LENGTH(TRIM(NEW.PLUGIN_ID)) = 0 OR NEW.PLUGIN_ID <> TRIM(NEW.PLUGIN_ID)
              OR LENGTH(TRIM(NEW.PRODUCT_DEFINITION_VERSION)) = 0 OR NEW.PRODUCT_DEFINITION_VERSION <> TRIM(NEW.PRODUCT_DEFINITION_VERSION)
              OR LENGTH(TRIM(NEW.PROGRAM_VERSION)) = 0 OR NEW.PROGRAM_VERSION <> TRIM(NEW.PROGRAM_VERSION)
              OR LENGTH(TRIM(NEW.PROGRAM_SCHEMA)) = 0 OR NEW.PROGRAM_SCHEMA <> TRIM(NEW.PROGRAM_SCHEMA)
              OR LENGTH(TRIM(NEW.BOUND_RECIPE_SNAPSHOT_SCHEMA)) = 0
              OR NEW.BOUND_RECIPE_SNAPSHOT_SCHEMA <> TRIM(NEW.BOUND_RECIPE_SNAPSHOT_SCHEMA)
              OR LENGTH(TRIM(NEW.RELEASED_BY)) = 0
              OR NEW.RELEASED_BY <> TRIM(NEW.RELEASED_BY)
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact identities and hashes are invalid');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_COORDINATE_BI
            BEFORE INSERT ON SYS_RELEASED_PROGRAM_ARTIFACT
            WHEN EXISTS (
              SELECT 1
                FROM SYS_RELEASED_PROGRAM_ARTIFACT A
               WHERE A.EQUIPMENT_ID COLLATE BINARY = NEW.EQUIPMENT_ID COLLATE BINARY
                 AND A.OPERATION_KEY COLLATE BINARY = NEW.OPERATION_KEY COLLATE BINARY
                 AND A.PRODUCT_PROFILE_ID COLLATE BINARY = NEW.PRODUCT_PROFILE_ID COLLATE BINARY
                 AND A.PLUGIN_ID COLLATE BINARY = NEW.PLUGIN_ID COLLATE BINARY
                 AND A.PRODUCT_DEFINITION_VERSION COLLATE BINARY
                       = NEW.PRODUCT_DEFINITION_VERSION COLLATE BINARY
                 AND A.PROGRAM_VERSION COLLATE BINARY = NEW.PROGRAM_VERSION COLLATE BINARY
                 AND A.PROGRAM_SCHEMA COLLATE BINARY = NEW.PROGRAM_SCHEMA COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact coordinate already has immutable content');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_REPLACE_BI
            BEFORE INSERT ON SYS_RELEASED_PROGRAM_ARTIFACT
            WHEN EXISTS (
              SELECT 1
                FROM SYS_RELEASED_PROGRAM_ARTIFACT A
               WHERE A.ARTIFACT_ID COLLATE BINARY = NEW.ARTIFACT_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact replacement is forbidden');
            END;
            """);

        if (!HasTable(connection, transaction, "SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION")) return;

        var invalidRevocation = FindInvalidRevocation(connection, transaction);
        if (invalidRevocation is not null)
        {
            throw new InvalidOperationException(
                "V159 released program artifact revocations contain invalid existing evidence: "
                + invalidRevocation);
        }

        foreach (var triggerName in new[]
        {
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_APPEND_ONLY_BU",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_APPEND_ONLY_BD",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_PARENT_BI",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_REPLACE_BI",
            "TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_VALIDATE_BI",
        })
        {
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {triggerName};");
        }

        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_APPEND_ONLY_BU
            BEFORE UPDATE ON SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact revocation is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_APPEND_ONLY_BD
            BEFORE DELETE ON SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact revocation is append-only');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_VALIDATE_BI
            BEFORE INSERT ON SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
            WHEN LENGTH(TRIM(NEW.REVOCATION_ID)) = 0
              OR LENGTH(NEW.REVOCATION_ID) > 100
              OR LENGTH(NEW.ARTIFACT_ID) > 200
              OR LENGTH(NEW.REVOKED_BY) > 50
              OR LENGTH(NEW.REASON) > 1000
              OR NEW.REVOCATION_ID <> TRIM(NEW.REVOCATION_ID)
              OR LENGTH(TRIM(NEW.ARTIFACT_ID)) = 0
              OR NEW.ARTIFACT_ID <> TRIM(NEW.ARTIFACT_ID)
              OR LENGTH(TRIM(NEW.REVOKED_BY)) = 0
              OR NEW.REVOKED_BY <> TRIM(NEW.REVOKED_BY)
              OR LENGTH(TRIM(NEW.REASON)) = 0
              OR NEW.REASON <> TRIM(NEW.REASON)
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact revocation provenance cannot be blank or have boundary spaces');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_PARENT_BI
            BEFORE INSERT ON SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
            WHEN NOT EXISTS (
              SELECT 1
                FROM SYS_RELEASED_PROGRAM_ARTIFACT A
               WHERE A.ARTIFACT_ID COLLATE BINARY = NEW.ARTIFACT_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'Program artifact revocation requires a released artifact');
            END;
            """);
        Execute(connection, transaction, """
            CREATE TRIGGER TR_SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION_REPLACE_BI
            BEFORE INSERT ON SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION
            WHEN EXISTS (
              SELECT 1
                FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
               WHERE R.REVOCATION_ID COLLATE BINARY = NEW.REVOCATION_ID COLLATE BINARY
                  OR R.ARTIFACT_ID COLLATE BINARY = NEW.ARTIFACT_ID COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'Released program artifact revocation replacement is forbidden');
            END;
            """);
    }

    private static string? FindInvalidArtifact(
        DbConnection connection,
        DbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ARTIFACT_ID
              FROM SYS_RELEASED_PROGRAM_ARTIFACT
             WHERE LENGTH(PROGRAM_HASH) <> 64
                OR PROGRAM_HASH GLOB '*[^0-9A-F]*'
                OR LENGTH(BOUND_RECIPE_SNAPSHOT_HASH) <> 64
                OR BOUND_RECIPE_SNAPSHOT_HASH GLOB '*[^0-9A-F]*'
                OR LENGTH(ARTIFACT_ID) > 200
                OR LENGTH(EQUIPMENT_ID) > 100
                OR LENGTH(OPERATION_KEY) > 200
                OR LENGTH(PRODUCT_PROFILE_ID) > 100
                OR LENGTH(PLUGIN_ID) > 200
                OR LENGTH(PRODUCT_DEFINITION_VERSION) > 100
                OR LENGTH(PROGRAM_VERSION) > 100
                OR LENGTH(PROGRAM_SCHEMA) > 100
                OR LENGTH(BOUND_RECIPE_SNAPSHOT_SCHEMA) > 100
                OR LENGTH(RELEASED_BY) > 50
                OR LENGTH(TRIM(ARTIFACT_ID)) = 0 OR ARTIFACT_ID <> TRIM(ARTIFACT_ID)
                OR LENGTH(TRIM(EQUIPMENT_ID)) = 0 OR EQUIPMENT_ID <> TRIM(EQUIPMENT_ID)
                OR LENGTH(TRIM(OPERATION_KEY)) = 0 OR OPERATION_KEY <> TRIM(OPERATION_KEY)
                OR LENGTH(TRIM(PRODUCT_PROFILE_ID)) = 0 OR PRODUCT_PROFILE_ID <> TRIM(PRODUCT_PROFILE_ID)
                OR LENGTH(TRIM(PLUGIN_ID)) = 0 OR PLUGIN_ID <> TRIM(PLUGIN_ID)
                OR LENGTH(TRIM(PRODUCT_DEFINITION_VERSION)) = 0
                OR PRODUCT_DEFINITION_VERSION <> TRIM(PRODUCT_DEFINITION_VERSION)
                OR LENGTH(TRIM(PROGRAM_VERSION)) = 0 OR PROGRAM_VERSION <> TRIM(PROGRAM_VERSION)
                OR LENGTH(TRIM(PROGRAM_SCHEMA)) = 0 OR PROGRAM_SCHEMA <> TRIM(PROGRAM_SCHEMA)
                OR LENGTH(TRIM(BOUND_RECIPE_SNAPSHOT_SCHEMA)) = 0
                OR BOUND_RECIPE_SNAPSHOT_SCHEMA <> TRIM(BOUND_RECIPE_SNAPSHOT_SCHEMA)
                OR LENGTH(TRIM(RELEASED_BY)) = 0 OR RELEASED_BY <> TRIM(RELEASED_BY)
             LIMIT 1;
            """;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static string? FindInvalidRevocation(
        DbConnection connection,
        DbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT R.REVOCATION_ID
              FROM SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
             WHERE LENGTH(R.REVOCATION_ID) > 100
                OR LENGTH(R.ARTIFACT_ID) > 200
                OR LENGTH(R.REVOKED_BY) > 50
                OR LENGTH(R.REASON) > 1000
                OR LENGTH(TRIM(R.REVOCATION_ID)) = 0 OR R.REVOCATION_ID <> TRIM(R.REVOCATION_ID)
                OR LENGTH(TRIM(R.ARTIFACT_ID)) = 0 OR R.ARTIFACT_ID <> TRIM(R.ARTIFACT_ID)
                OR LENGTH(TRIM(R.REVOKED_BY)) = 0 OR R.REVOKED_BY <> TRIM(R.REVOKED_BY)
                OR LENGTH(TRIM(R.REASON)) = 0 OR R.REASON <> TRIM(R.REASON)
                OR NOT EXISTS (
                    SELECT 1
                      FROM SYS_RELEASED_PROGRAM_ARTIFACT A
                     WHERE A.ARTIFACT_ID COLLATE BINARY = R.ARTIFACT_ID COLLATE BINARY)
             LIMIT 1;
            """;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static string? FindDuplicateCoordinate(
        DbConnection connection,
        DbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MIN(ARTIFACT_ID)
              FROM SYS_RELEASED_PROGRAM_ARTIFACT
             GROUP BY EQUIPMENT_ID COLLATE BINARY,
                      OPERATION_KEY COLLATE BINARY,
                      PRODUCT_PROFILE_ID COLLATE BINARY,
                      PLUGIN_ID COLLATE BINARY,
                      PRODUCT_DEFINITION_VERSION COLLATE BINARY,
                      PROGRAM_VERSION COLLATE BINARY,
                      PROGRAM_SCHEMA COLLATE BINARY
            HAVING COUNT(*) > 1
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
