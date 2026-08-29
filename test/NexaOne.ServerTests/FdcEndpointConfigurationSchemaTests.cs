using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>V145 PLC endpoint configuration의 SQLite fresh/incremental 동등성과 fail-closed guard를 검증한다.</summary>
public sealed class FdcEndpointConfigurationSchemaTests
{
    private static readonly string[] V145Columns =
    [
        "MODBUS_UNIT_ID",
        "S7_RACK",
        "S7_SLOT",
        "MITSUBISHI_STATION_NO",
        "MITSUBISHI_NETWORK_NO",
        "MITSUBISHI_PC_NO",
        "MITSUBISHI_IO_NO",
        "MITSUBISHI_FRAME_FORMAT",
        "CONNECTION_TIMEOUT_MS",
        "READ_WRITE_TIMEOUT_MS",
        "HEARTBEAT_TIMEOUT_MS",
        "POLLING_DISCONNECT_BACKOFF_MS",
        "POLLING_MAX_DISCONNECT_BACKOFF_MS",
    ];

    [Fact]
    public void Fresh_schema_installs_structured_columns_defaults_and_integrity_triggers()
    {
        var cs = NewDatabase();
        try
        {
            SqliteSchemaInitializer.Apply(cs);

            Columns(cs).Should().Contain(V145Columns);
            InsertEndpoint(cs, "EP-FRESH", "MitsubishiMc", "tcp://plc:5007",
                extraColumns: ", MITSUBISHI_STATION_NO, MITSUBISHI_NETWORK_NO, MITSUBISHI_PC_NO, MITSUBISHI_IO_NO, MITSUBISHI_FRAME_FORMAT",
                extraValues: ", 3, 1, 255, 1023, 'Ascii'");

            Scalar(cs, """
                SELECT CONNECTION_TIMEOUT_MS || ':' || READ_WRITE_TIMEOUT_MS || ':' ||
                       HEARTBEAT_TIMEOUT_MS || ':' || POLLING_DISCONNECT_BACKOFF_MS || ':' ||
                       POLLING_MAX_DISCONNECT_BACKOFF_MS
                  FROM FDC_EQUIPMENT_ENDPOINT WHERE ENDPOINT_ID = 'EP-FRESH';
                """).Should().Be("5000:5000:5000:100:1000");
            TriggerExists(cs, "TR_FDC_ENDPOINT_CONFIG_VALIDATE_INSERT").Should().BeTrue();
            TriggerExists(cs, "TR_FDC_ENDPOINT_CONFIG_VALIDATE_UPDATE").Should().BeTrue();

            Action invalidTimeout = () => Execute(cs,
                "UPDATE FDC_EQUIPMENT_ENDPOINT SET CONNECTION_TIMEOUT_MS=0 WHERE ENDPOINT_ID='EP-FRESH';");
            invalidTimeout.Should().Throw<SqliteException>().WithMessage("*V145 FDC PLC endpoint configuration is invalid*");

            Action protocolMismatch = () => InsertEndpoint(
                cs, "EP-MISMATCH", "EtherNetIp", "tcp://plc:44818",
                extraColumns: ", MODBUS_UNIT_ID", extraValues: ", 1");
            protocolMismatch.Should().Throw<SqliteException>().WithMessage("*V145 FDC PLC endpoint configuration is invalid*");

            Action rawPath = () => InsertEndpoint(cs, "EP-RAW-PATH", "ModbusTcp", "plc:502/config");
            rawPath.Should().Throw<SqliteException>().WithMessage("*V145 FDC PLC endpoint configuration is invalid*");

            Action explicitRootPath = () => InsertEndpoint(cs, "EP-ROOT-PATH", "ModbusTcp", "tcp://plc:502/");
            explicitRootPath.Should().Throw<SqliteException>().WithMessage("*V145 FDC PLC endpoint configuration is invalid*");

            Action backslashPath = () => InsertEndpoint(cs, "EP-BACKSLASH-PATH", "ModbusTcp", "tcp://plc:502\\config");
            backslashPath.Should().Throw<SqliteException>()
                .WithMessage("*V145 FDC PLC endpoint configuration is invalid*");

            Action unsupportedScheme = () => InsertEndpoint(cs, "EP-HTTP", "ModbusTcp", "http://plc:80");
            unsupportedScheme.Should().Throw<SqliteException>()
                .WithMessage("*V145 FDC PLC endpoint configuration is invalid*");
        }
        finally { DeleteDatabase(cs); }
    }

    [Fact]
    public void Incremental_schema_preserves_rows_and_reconciles_v145_defaults_and_guards()
    {
        var cs = NewDatabase();
        try
        {
            CreatePreV145EndpointTable(cs);
            InsertEndpoint(cs, "EP-LEGACY", "ModbusTcp", "tcp://legacy-plc:502");

            SqliteSchemaInitializer.EnsureSchema(cs);

            Columns(cs).Should().Contain(V145Columns);
            Scalar(cs, """
                SELECT CONNECTION_TIMEOUT_MS || ':' || READ_WRITE_TIMEOUT_MS || ':' ||
                       HEARTBEAT_TIMEOUT_MS || ':' || POLLING_DISCONNECT_BACKOFF_MS || ':' ||
                       POLLING_MAX_DISCONNECT_BACKOFF_MS
                  FROM FDC_EQUIPMENT_ENDPOINT WHERE ENDPOINT_ID = 'EP-LEGACY';
                """).Should().Be("5000:5000:5000:100:1000");

            Action inlineSecret = () => Execute(cs, """
                UPDATE FDC_EQUIPMENT_ENDPOINT
                   SET ENDPOINT_URL='tcp://operator:plain-password@plc:502'
                 WHERE ENDPOINT_ID='EP-LEGACY';
                """);
            inlineSecret.Should().Throw<SqliteException>()
                .WithMessage("*V145 FDC PLC endpoint configuration is invalid*");
        }
        finally { DeleteDatabase(cs); }
    }

    [Fact]
    public void Migration_exposes_only_explicit_secret_free_allowlisted_columns()
    {
        var sql = File.ReadAllText(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "config", "db", "migrations",
            "V145__FDC_PLC_ENDPOINT_CONFIGURATION.sql"));

        sql.Should().StartWith("-- Owner: FDC.");
        sql.Should().Contain("CONNECTION_TIMEOUT_MS > 0");
        sql.Should().Contain("POLLING_MAX_DISCONNECT_BACKOFF_MS >= POLLING_DISCONNECT_BACKOFF_MS");
        sql.Should().Contain("UPPER(PROTOCOL) = 'MODBUSTCP'",
            "domain protocol matching is case-insensitive");
        sql.Should().Contain("CK_FDC_ENDPOINT_NO_INLINE_SECRET");
        sql.Should().Contain("UPPER(LEFT(LTRIM(RTRIM(ENDPOINT_URL)), 6)) = 'TCP://'",
            "only tcp:// or a scheme-less host may reach the four polling drivers");
        sql.Should().Contain("ENDPOINT_URL NOT LIKE '%://%' AND ENDPOINT_URL NOT LIKE '%/%'",
            "a scheme-less host:port/path payload must be rejected like domain and SQLite validation");
        sql.Should().NotContain("PASSWORD", "secrets must be injected outside the FDC database");
        sql.Should().NotContain("USERNAME", "secrets must be injected outside the FDC database");
        sql.Should().NotContain("OPTIONS_JSON", "only consumed driver options receive explicit columns");
        sql.Should().NotContain("OPC_UA", "FDC keeps only the four atomic polling protocols");
    }

    private static string NewDatabase() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), $"nexa-fdc-v145-{Guid.NewGuid():N}.db")};Foreign Keys=False";

    private static string FilePath(string connectionString) =>
        connectionString.Replace("Data Source=", "", StringComparison.Ordinal).Split(';')[0];

    private static void DeleteDatabase(string connectionString)
    {
        try { File.Delete(FilePath(connectionString)); } catch { /* best-effort temporary database cleanup */ }
    }

    private static IReadOnlyList<string> Columns(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(FDC_EQUIPMENT_ENDPOINT);";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    private static string Scalar(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static bool TriggerExists(string connectionString, string triggerName)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name=@name;";
        command.Parameters.AddWithValue("@name", triggerName);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 1;
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertEndpoint(
        string connectionString,
        string endpointId,
        string protocol,
        string endpointUrl,
        string extraColumns = "",
        string extraValues = "")
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO FDC_EQUIPMENT_ENDPOINT
                (ENDPOINT_ID, EQUIPMENT_ID, PROTOCOL, ENDPOINT_URL,
                 SAMPLING_INTERVAL_MS, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT
                 {extraColumns})
            VALUES
                (@endpointId, 'EQ-V145', @protocol, @endpointUrl,
                 500, 1, 'test', CURRENT_TIMESTAMP, 'test', CURRENT_TIMESTAMP
                 {extraValues});
            """;
        command.Parameters.AddWithValue("@endpointId", endpointId);
        command.Parameters.AddWithValue("@protocol", protocol);
        command.Parameters.AddWithValue("@endpointUrl", endpointUrl);
        command.ExecuteNonQuery();
    }

    private static void CreatePreV145EndpointTable(string connectionString) => Execute(connectionString, """
        CREATE TABLE FDC_EQUIPMENT_ENDPOINT (
            ENDPOINT_ID TEXT NOT NULL PRIMARY KEY,
            EQUIPMENT_ID TEXT NOT NULL,
            PROTOCOL TEXT NOT NULL,
            ENDPOINT_URL TEXT NOT NULL,
            TAG_MAP_PATH TEXT NULL,
            SAMPLING_INTERVAL_MS INTEGER NOT NULL DEFAULT 1000,
            IS_ACTIVE INTEGER NOT NULL DEFAULT 1,
            CREATED_BY TEXT NOT NULL,
            CREATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            UPDATED_BY TEXT NOT NULL,
            UPDATED_AT TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
        """);
}
