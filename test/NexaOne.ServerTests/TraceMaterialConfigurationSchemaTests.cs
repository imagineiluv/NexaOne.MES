using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>V151 TRACE binding/feed-session command ledger의 fresh/incremental SQLite와 MSSQL DDL 계약입니다.</summary>
public sealed class TraceMaterialConfigurationSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V151_sqlite_fresh_and_incremental_paths_have_equivalent_versioned_ledgers(
        bool incremental)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-v151-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            if (incremental)
            {
                Execute(connectionString,
                    "CREATE TABLE LEGACY_BOOT_MARKER (ID INTEGER NOT NULL PRIMARY KEY);");
                SqliteSchemaInitializer.EnsureSchema(connectionString);
            }
            else
            {
                SqliteSchemaInitializer.Apply(connectionString);
            }

            ColumnCount(connectionString, "IVT_TRACE_CONSUMPTION_BINDING", "VERSION_NO")
                .Should().Be(1);
            ColumnCount(connectionString, "IVT_MATERIAL_FEED_SESSION", "VERSION_NO")
                .Should().Be(1);
            ColumnCount(connectionString, "IVT_MATERIAL_LOT", "ACTIVE_FEED_SESSION_ID")
                .Should().Be(1);
            ColumnCount(connectionString, "IVT_MATERIAL_CONSUMPTION_HISTORY", "FEED_SESSION_ID")
                .Should().Be(1);
            TableExists(connectionString, "IVT_TRACE_BINDING_COMMAND").Should().BeTrue();
            TableExists(connectionString, "IVT_FEED_SESSION_COMMAND").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_TRACE_BINDING_COMMAND_IDEMPOTENCY").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_TRACE_BINDING_COMMAND_SOURCE").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_FEED_SESSION_COMMAND_IDEMPOTENCY").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_FEED_SESSION_COMMAND_SOURCE").Should().BeTrue();
            IndexExists(connectionString, "IX_IVT_TRACE_BINDING_INTERVAL").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_TRACE_BINDING_ACTIVE_SOURCE").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_FEED_SESSION_ACTIVE_LOT").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_FEED_SESSION_ID_MATERIAL_LOT").Should().BeTrue();
            IndexExists(connectionString, "UX_IVT_MATERIAL_LOT_ACTIVE_FEED_SESSION").Should().BeTrue();
            IndexExists(connectionString, "IX_IVT_MATERIAL_CONSUMPTION_FEED_SESSION").Should().BeTrue();
            IndexExists(connectionString, "IX_IVT_TRACE_INBOX_FEED_EVIDENCE").Should().BeFalse();
            IndexExists(connectionString, "IX_IVT_FEED_SESSION_INTERVAL").Should().BeFalse();
            IndexExists(connectionString, "IX_IVT_TRACE_BINDING_SOURCE").Should().BeFalse();
            IndexColumns(connectionString, "UX_IVT_TRACE_BINDING_ACTIVE_SOURCE")
                .Should().Equal("EQUIPMENT_ID", "PARAMETER_ID");
            IndexColumns(connectionString, "IX_IVT_TRACE_BINDING_INTERVAL")
                .Should().Equal("EQUIPMENT_ID", "PARAMETER_ID", "EFFECTIVE_TO");
            TriggerExists(connectionString, "TR_IVT_MATERIAL_CONSUMPTION_FEED_SESSION_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_CONSUMPTION_FEED_SESSION_BU").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_FEED_SESSION_CONSUMPTION_BU").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_FEED_SESSION_CONSUMPTION_BD").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_LOT_FEED_RESERVATION_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_LOT_FEED_RESERVATION_BU").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_LOT_REFERENCE_REPLACE_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_LOT_REFERENCE_BD").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_FEED_SESSION_RESERVE_LOT_AI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_FEED_SESSION_RESERVATION_BU").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_FEED_SESSION_RESERVATION_BD").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_MATERIAL_FEED_SESSION_REPLACE_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_TRACE_BINDING_REPLACE_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_TRACE_BINDING_COMMAND_PARENT_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_TRACE_BINDING_COMMAND_PARENT_BD").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_FEED_SESSION_COMMAND_PARENT_BI").Should().BeTrue();
            TriggerExists(connectionString, "TR_IVT_FEED_SESSION_COMMAND_PARENT_BD").Should().BeTrue();

            SqliteSchemaInitializer.EnsureSchema(connectionString);
            ColumnCount(connectionString, "IVT_TRACE_CONSUMPTION_BINDING", "VERSION_NO")
                .Should().Be(1, "restart must not duplicate an ALTER ADD COLUMN");
            ColumnCount(connectionString, "IVT_MATERIAL_FEED_SESSION", "VERSION_NO")
                .Should().Be(1, "restart must not duplicate an ALTER ADD COLUMN");
            TriggerExists(connectionString, "TR_IVT_MATERIAL_LOT_REFERENCE_REPLACE_BI")
                .Should().BeTrue("restart must restore the canonical replace guard");
            TriggerExists(connectionString, "TR_IVT_MATERIAL_FEED_SESSION_REPLACE_BI")
                .Should().BeTrue("restart must restore the canonical replace guard");
            TriggerExists(connectionString, "TR_IVT_TRACE_BINDING_REPLACE_BI")
                .Should().BeTrue("restart must restore the canonical replace guard");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort temporary file cleanup */ }
        }
    }

    [Fact]
    public void V151_sqlite_reservation_requires_the_feed_session_to_belong_to_the_same_lot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-v151-reservation-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            SqliteSchemaInitializer.Apply(connectionString);
            Execute(connectionString, """
                INSERT INTO IVT_MATERIAL_LOT (LOT_ID, MATERIAL_ID, CURRENT_QTY, STATUS)
                VALUES ('LOT-A', 'MAT-1', 10, 'InStock'), ('LOT-B', 'MAT-1', 10, 'InStock');
                INSERT INTO IVT_MATERIAL_FEED_SESSION
                    (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
                     MATERIAL_LOT_ID, MATERIAL_ID, MOUNTED_AT, MOUNTED_BY, STATUS)
                VALUES
                    ('SESSION-A', 'P1', 'EQ1', 'FEED-1', 'LOT-A', 'MAT-1',
                     '2026-08-28 00:00:00.0000000', 'operator', 'Mounted');
                UPDATE IVT_MATERIAL_LOT
                   SET ACTIVE_FEED_SESSION_ID='SESSION-A'
                 WHERE LOT_ID='LOT-A';
                """);

            Action wrongLot = () => Execute(connectionString,
                "UPDATE IVT_MATERIAL_LOT SET ACTIVE_FEED_SESSION_ID='SESSION-MISSING' WHERE LOT_ID='LOT-B';");
            wrongLot.Should().Throw<SqliteException>()
                .WithMessage("*reservation feed session does not match the LOT*");

            Action clearPendingDrain = () => Execute(connectionString,
                "UPDATE IVT_MATERIAL_LOT SET ACTIVE_FEED_SESSION_ID=NULL WHERE LOT_ID='LOT-A';");
            clearPendingDrain.Should().Throw<SqliteException>()
                .WithMessage("*reservation feed session does not match the LOT*");

            Execute(connectionString, """
                UPDATE IVT_MATERIAL_FEED_SESSION
                   SET STATUS='Unmounted',
                       UNMOUNTED_AT='2026-08-28 00:01:00.0000000',
                       UNMOUNTED_BY='operator'
                 WHERE FEED_SESSION_ID='SESSION-A';
                """);
            Scalar(connectionString,
                    "SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID='LOT-A' AND ACTIVE_FEED_SESSION_ID='SESSION-A';")
                .Should().Be(1, "Unmount keeps the PendingDrain reservation until durable finalization exists");

            Action moveParent = () => Execute(connectionString,
                "UPDATE IVT_MATERIAL_FEED_SESSION SET MATERIAL_LOT_ID='LOT-B' WHERE FEED_SESSION_ID='SESSION-A';");
            moveParent.Should().Throw<SqliteException>()
                .WithMessage("*active LOT reservation*");
            Action deleteParent = () => Execute(connectionString,
                "DELETE FROM IVT_MATERIAL_FEED_SESSION WHERE FEED_SESSION_ID='SESSION-A';");
            deleteParent.Should().Throw<SqliteException>()
                .WithMessage("*active LOT reservation*");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort temporary file cleanup */ }
        }
    }

    [Fact]
    public void V151_sqlite_foreign_key_off_still_rejects_parent_replace_and_referenced_lot_delete()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-v151-replace-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            SqliteSchemaInitializer.Apply(connectionString);
            Execute(connectionString, """
                INSERT INTO IVT_MATERIAL_LOT (LOT_ID, MATERIAL_ID, CURRENT_QTY, STATUS)
                VALUES ('LOT-A', 'MAT-1', 10, 'InStock'), ('LOT-B', 'MAT-1', 10, 'InStock');
                INSERT INTO IVT_TRACE_CONSUMPTION_BINDING
                    (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
                     CALCULATION_MODE, SCALE_FACTOR, OUTPUT_UNIT, EFFECTIVE_FROM, IS_ACTIVE)
                VALUES ('BIND-A', 'P1', 'EQ1', 'FLOW', 'FEED-1', 'Direct', 1, 'kg',
                        '2026-08-28 00:00:00.0000000', 1);
                INSERT INTO IVT_MATERIAL_FEED_SESSION
                    (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
                     MATERIAL_LOT_ID, MATERIAL_ID, MOUNTED_AT, MOUNTED_BY, STATUS)
                VALUES ('SESSION-A', 'P1', 'EQ1', 'FEED-1', 'LOT-A', 'MAT-1',
                        '2026-08-28 00:00:00.0000000', 'operator', 'Mounted');
                """);

            Action replaceBinding = () => Execute(connectionString, """
                INSERT OR REPLACE INTO IVT_TRACE_CONSUMPTION_BINDING
                    (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
                     CALCULATION_MODE, SCALE_FACTOR, OUTPUT_UNIT, EFFECTIVE_FROM, IS_ACTIVE)
                VALUES ('BIND-B', 'P2', 'EQ1', 'FLOW', 'FEED-2', 'Direct', 1, 'kg',
                        '2026-08-28 00:00:00.0000000', 1);
                """);
            replaceBinding.Should().Throw<SqliteException>();
            Scalar(connectionString,
                    "SELECT COUNT(*) FROM IVT_TRACE_CONSUMPTION_BINDING WHERE BINDING_ID='BIND-A';")
                .Should().Be(1);

            Action replaceSession = () => Execute(connectionString, """
                INSERT OR REPLACE INTO IVT_MATERIAL_FEED_SESSION
                    (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
                     MATERIAL_LOT_ID, MATERIAL_ID, MOUNTED_AT, MOUNTED_BY, STATUS)
                VALUES ('SESSION-B', 'P1', 'EQ1', 'FEED-1', 'LOT-B', 'MAT-1',
                        '2026-08-28 00:00:00.0000000', 'operator', 'Mounted');
                """);
            replaceSession.Should().Throw<SqliteException>();
            Scalar(connectionString,
                    "SELECT COUNT(*) FROM IVT_MATERIAL_FEED_SESSION WHERE FEED_SESSION_ID='SESSION-A';")
                .Should().Be(1);

            Action deleteLot = () => Execute(connectionString,
                "DELETE FROM IVT_MATERIAL_LOT WHERE LOT_ID='LOT-A';");
            deleteLot.Should().Throw<SqliteException>();

            Action replaceLot = () => Execute(connectionString, """
                INSERT OR REPLACE INTO IVT_MATERIAL_LOT
                    (LOT_ID, MATERIAL_ID, CURRENT_QTY, STATUS)
                VALUES ('LOT-A', 'MAT-1', 99, 'InStock');
                """);
            replaceLot.Should().Throw<SqliteException>();
            Scalar(connectionString,
                    "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID='LOT-A';")
                .Should().Be(10);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort temporary file cleanup */ }
        }
    }

    [Fact]
    public void V151_sqlite_restart_rejects_a_corrupted_pending_drain_reservation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-v151-reservation-restart-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            SqliteSchemaInitializer.Apply(connectionString);
            Execute(connectionString, """
                INSERT INTO IVT_MATERIAL_LOT (LOT_ID, MATERIAL_ID, CURRENT_QTY, STATUS)
                VALUES ('LOT-A', 'MAT-1', 10, 'InStock'), ('LOT-B', 'MAT-1', 10, 'InStock');
                INSERT INTO IVT_MATERIAL_FEED_SESSION
                    (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
                     MATERIAL_LOT_ID, MATERIAL_ID, MOUNTED_AT, MOUNTED_BY, STATUS)
                VALUES
                    ('SESSION-A', 'P1', 'EQ1', 'FEED-1', 'LOT-A', 'MAT-1',
                     '2026-08-28 00:00:00.0000000', 'operator', 'Mounted');
                UPDATE IVT_MATERIAL_FEED_SESSION
                   SET STATUS='Unmounted',
                       UNMOUNTED_AT='2026-08-28 00:01:00.0000000',
                       UNMOUNTED_BY='operator'
                 WHERE FEED_SESSION_ID='SESSION-A';
                DROP TRIGGER TR_IVT_MATERIAL_LOT_FEED_RESERVATION_BU;
                UPDATE IVT_MATERIAL_LOT
                   SET ACTIVE_FEED_SESSION_ID=NULL
                 WHERE LOT_ID='LOT-A';
                UPDATE IVT_MATERIAL_LOT
                   SET ACTIVE_FEED_SESSION_ID='SESSION-A'
                 WHERE LOT_ID='LOT-B';
                """);

            Action restart = () => SqliteSchemaInitializer.EnsureSchema(connectionString);
            restart.Should().Throw<InvalidOperationException>()
                .WithMessage("*legacy Unmounted*without an audited PendingDrain*");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort temporary file cleanup */ }
        }
    }

    [Fact]
    public void V151_sqlite_upgrade_preserves_the_V114_index_when_the_new_source_key_has_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexa-v151-duplicate-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Foreign Keys=False";
        try
        {
            SqliteSchemaInitializer.Apply(connectionString);
            Execute(connectionString, """
                -- Emulate a pre-V151 database before reconstructing the legacy source key.
                DROP TRIGGER TR_IVT_TRACE_BINDING_REPLACE_BI;
                DROP INDEX UX_IVT_TRACE_BINDING_ACTIVE_SOURCE;
                CREATE UNIQUE INDEX UX_IVT_TRACE_BINDING_ACTIVE_SOURCE
                    ON IVT_TRACE_CONSUMPTION_BINDING
                       (PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID)
                    WHERE IS_ACTIVE = 1;
                INSERT INTO IVT_TRACE_CONSUMPTION_BINDING
                    (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
                     CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT,
                     EFFECTIVE_FROM, IS_ACTIVE)
                VALUES
                    ('LEGACY-B1', 'P1', 'EQ1', 'FLOW', 'F1', 'Direct', 1, NULL, 'kg',
                     '2026-08-27 00:00:00.0000000', 1),
                    ('LEGACY-B2', 'P2', 'EQ1', 'FLOW', 'F2', 'Direct', 1, NULL, 'kg',
                     '2026-08-27 00:00:00.0000000', 1);
                """);

            Action upgrade = () => SqliteSchemaInitializer.EnsureSchema(connectionString);

            upgrade.Should().Throw<InvalidOperationException>()
                .WithMessage("*duplicate active TRACE source*");
            IndexColumns(connectionString, "UX_IVT_TRACE_BINDING_ACTIVE_SOURCE")
                .Should().Equal(
                    new[] { "PLANT_ID", "EQUIPMENT_ID", "PARAMETER_ID", "FEED_POINT_ID" },
                    "the failed upgrade must preserve the V114 safety index");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort temporary file cleanup */ }
        }
    }

    [Fact]
    public void V151_mssql_migration_defines_the_same_cas_and_audit_keys()
    {
        var sql = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V151__IVT_TRACE_MATERIAL_CONFIGURATION_COMMANDS.sql"));

        sql.Should().StartWith("-- Owner: IVT.");
        sql.Should().Contain("ALTER TABLE IVT_TRACE_CONSUMPTION_BINDING ADD VERSION_NO INT NOT NULL DEFAULT 1");
        sql.Should().Contain("ALTER TABLE IVT_MATERIAL_FEED_SESSION ADD VERSION_NO INT NOT NULL DEFAULT 1");
        sql.Should().Contain("ALTER TABLE IVT_MATERIAL_LOT ADD ACTIVE_FEED_SESSION_ID");
        sql.Should().Contain("ALTER TABLE IVT_MATERIAL_CONSUMPTION_HISTORY ADD FEED_SESSION_ID");
        sql.Should().Contain("GROUP BY EQUIPMENT_ID, PARAMETER_ID");
        sql.Should().Contain("GROUP BY MATERIAL_LOT_ID");
        Regex.Matches(sql, "HAVING COUNT_BIG\\(\\*\\) > 1", RegexOptions.CultureInvariant)
            .Should().HaveCount(2);
        sql.Should().Contain("THROW 51513");
        sql.Should().Contain("THROW 51514");
        sql.Should().Contain("THROW 51519");
        sql.Should().Contain("legacy Unmounted feed sessions require audited PendingDrain reconciliation");
        sql.Should().Contain("CREATE TABLE IVT_TRACE_BINDING_COMMAND");
        sql.Should().Contain("CREATE TABLE IVT_FEED_SESSION_COMMAND");
        sql.Should().Contain("COMMAND_TYPE IN ('Create', 'Retire')");
        sql.Should().Contain("COMMAND_TYPE IN ('Mount', 'Unmount')");
        sql.Should().NotContain("'Cancel'");
        sql.Should().NotContain("'Cancelled'");
        sql.Should().Contain("RESULT_VERSION = EXPECTED_VERSION + 1");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_IVT_TRACE_BINDING_COMMAND_IDEMPOTENCY");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_IVT_TRACE_BINDING_COMMAND_SOURCE");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_IVT_FEED_SESSION_COMMAND_IDEMPOTENCY");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_IVT_FEED_SESSION_COMMAND_SOURCE");
        sql.Should().Contain("CREATE INDEX IX_IVT_TRACE_BINDING_INTERVAL");
        sql.Should().Contain("DROP INDEX UX_IVT_TRACE_BINDING_ACTIVE_SOURCE ON IVT_TRACE_CONSUMPTION_BINDING");
        sql.Should().Contain("DROP INDEX IX_IVT_TRACE_BINDING_SOURCE ON IVT_TRACE_CONSUMPTION_BINDING");
        sql.Should().Contain("ON IVT_TRACE_CONSUMPTION_BINDING (EQUIPMENT_ID, PARAMETER_ID)");
        sql.Should().Contain("(EQUIPMENT_ID, PARAMETER_ID, EFFECTIVE_TO)");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_IVT_FEED_SESSION_ACTIVE_LOT");
        sql.Should().Contain("CREATE UNIQUE INDEX UX_IVT_FEED_SESSION_ID_MATERIAL_LOT");
        sql.Should().Contain("FK_IVT_MATERIAL_LOT_ACTIVE_FEED_SESSION FOREIGN KEY (ACTIVE_FEED_SESSION_ID, LOT_ID)");
        sql.Should().Contain("REFERENCES IVT_MATERIAL_FEED_SESSION (FEED_SESSION_ID, MATERIAL_LOT_ID)");
        sql.Should().Contain("FK_IVT_MATERIAL_CONSUMPTION_FEED_SESSION FOREIGN KEY (FEED_SESSION_ID, MATERIAL_LOT_ID)");
        sql.Should().Contain("CREATE INDEX IX_IVT_MATERIAL_CONSUMPTION_FEED_SESSION");
        sql.Should().NotContain("SET FEED_SESSION_ID = CORRELATION_ID",
            "V137 material-consumption evidence is append-only and legacy provenance must remain immutable");
        sql.Should().NotContain("CREATE INDEX IX_IVT_TRACE_INBOX_FEED_EVIDENCE");
        sql.Should().NotContain("CREATE INDEX IX_IVT_FEED_SESSION_INTERVAL");
        Regex.Matches(sql, "COLLATE Latin1_General_100_BIN2", RegexOptions.CultureInvariant)
            .Should().HaveCount(8,
                "idempotency and source identities must remain ordinal in both database providers");
        sql.Should().MatchRegex("COMMAND_TYPE = 'Create'[\\s\\S]*?EFFECTIVE_TO IS NULL");
        sql.Should().MatchRegex("COMMAND_TYPE = 'Retire'[\\s\\S]*?EFFECTIVE_TO IS NOT NULL");
        sql.Should().MatchRegex("COMMAND_TYPE = 'Unmount'[\\s\\S]*?RESULT_STATUS = 'Unmounted'");
        sql.Should().Contain("UNMOUNTED_AT IS NOT NULL AND UNMOUNTED_BY IS NOT NULL");
        sql.Should().Contain("CREATE TRIGGER TR_IVT_TRACE_BINDING_COMMAND_APPEND_ONLY");
        sql.Should().Contain("CREATE TRIGGER TR_IVT_FEED_SESSION_COMMAND_APPEND_ONLY");
        sql.Should().Contain("CREATE TRIGGER TR_IVT_FEED_SESSION_RESERVE_LOT");
        sql.Should().Contain("CREATE TRIGGER TR_IVT_MATERIAL_LOT_FEED_RESERVATION_GUARD");
        sql.Should().Contain("PendingDrain feed-session reservation cannot be cleared or replaced");
        sql.Should().Contain("IVT_TRACE_BINDING_COMMAND is append-only");
        sql.Should().Contain("IVT_FEED_SESSION_COMMAND is append-only");
        sql.Should().Contain("DATETIME2");
        sql.Should().NotContain("INSERT OR ", "the source migration must remain valid SQL Server DDL");
        sql.Should().NotContain("AUTOINCREMENT", "the source migration must remain valid SQL Server DDL");
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int ColumnCount(string connectionString, string table, string column) =>
        Scalar(connectionString,
            "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name=@column;",
            ("@table", table), ("@column", column));

    private static bool TableExists(string connectionString, string table) =>
        Scalar(connectionString,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;",
            ("@name", table)) == 1;

    private static bool IndexExists(string connectionString, string index) =>
        Scalar(connectionString,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name;",
            ("@name", index)) == 1;

    private static bool TriggerExists(string connectionString, string trigger) =>
        Scalar(connectionString,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name=@name;",
            ("@name", trigger)) == 1;

    private static IReadOnlyList<string> IndexColumns(string connectionString, string index)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info([{index.Replace("]", "]]", StringComparison.Ordinal)}]);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(2));
        return columns;
    }

    private static int Scalar(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
