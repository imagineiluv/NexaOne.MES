using Microsoft.Data.Sqlite;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.UnitTests.TestInfrastructure;

namespace NexaOne.UnitTests.Ivt;

public sealed class TraceIngestionPersistenceTests
{
    [Fact]
    public async Task Persisted_inbox_cursor_makes_repoll_idempotent_and_keeps_equal_timestamp_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-ivt-ingestion-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        var t0 = new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc);
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE FDC_COLLECT_DATA (
                        COLLECT_ID TEXT NOT NULL PRIMARY KEY,
                        EQUIPMENT_ID TEXT NOT NULL,
                        PARAMETER_ID TEXT NOT NULL,
                        VALUE NUMERIC NOT NULL,
                        COLLECTED_AT TEXT NOT NULL,
                        QUALITY TEXT NOT NULL,
                        LOWER_LIMIT NUMERIC NOT NULL,
                        UPPER_LIMIT NUMERIC NOT NULL);

                    CREATE TABLE IVT_TRACE_CONSUMPTION_BINDING (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        PLANT_ID TEXT NOT NULL,
                        EQUIPMENT_ID TEXT NOT NULL,
                        PARAMETER_ID TEXT NOT NULL,
                        FEED_POINT_ID TEXT NOT NULL,
                        CALCULATION_MODE TEXT NOT NULL,
                        SCALE_FACTOR NUMERIC NOT NULL,
                        PULSE_QUANTITY NUMERIC NULL,
                        OUTPUT_UNIT TEXT NOT NULL,
                        EFFECTIVE_FROM TEXT NOT NULL,
                        EFFECTIVE_TO TEXT NULL,
                        IS_ACTIVE INTEGER NOT NULL);

                    CREATE TABLE IVT_TRACE_PROJECTION_INBOX (
                        BINDING_ID TEXT NOT NULL,
                        COLLECT_ID TEXT NOT NULL,
                        PLANT_ID TEXT NOT NULL,
                        EQUIPMENT_ID TEXT NOT NULL,
                        PARAMETER_ID TEXT NOT NULL,
                        FEED_POINT_ID TEXT NOT NULL,
                        CALCULATION_MODE TEXT NOT NULL,
                        SCALE_FACTOR NUMERIC NOT NULL,
                        PULSE_QUANTITY NUMERIC NULL,
                        OUTPUT_UNIT TEXT NOT NULL,
                        RAW_VALUE NUMERIC NOT NULL,
                        QUALITY TEXT NOT NULL,
                        COLLECTED_AT TEXT NOT NULL,
                        STATUS TEXT NOT NULL,
                        ATTEMPT_COUNT INTEGER NOT NULL,
                        IS_WORK_ITEM INTEGER NOT NULL DEFAULT 1,
                        CREATED_BY TEXT NOT NULL,
                        CREATED_AT TEXT NOT NULL,
                        UPDATED_BY TEXT NOT NULL,
                        UPDATED_AT TEXT NOT NULL,
                        PRIMARY KEY (BINDING_ID, COLLECT_ID));

                    CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        LAST_COLLECT_ID TEXT NOT NULL,
                        LAST_COLLECTED_AT TEXT NOT NULL,
                        CREATED_BY TEXT NOT NULL,
                        CREATED_AT TEXT NOT NULL,
                        UPDATED_BY TEXT NOT NULL,
                        UPDATED_AT TEXT NOT NULL);

                    INSERT INTO IVT_TRACE_CONSUMPTION_BINDING VALUES
                        ('BIND-1', 'PLANT-1', 'EQ-1', 'PARAM-1', 'FEED-1',
                         'CounterDelta', 1, NULL, 'kg', @effectiveFrom, NULL, 1);
                    INSERT INTO FDC_COLLECT_DATA VALUES
                        ('C-1', 'EQ-1', 'PARAM-1', 10, @t1, 'Good', 0, 100),
                        ('C-2', 'EQ-1', 'PARAM-1', 12.5, @t2, 'Good', 0, 100);
                    """;
                command.Parameters.AddWithValue("@effectiveFrom", t0.AddMinutes(-1));
                command.Parameters.AddWithValue("@t1", t0.AddSeconds(1));
                command.Parameters.AddWithValue("@t2", t0.AddSeconds(2));
                command.ExecuteNonQuery();
            }

            var dataSource = new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            };
            var dialect = new SqliteEesDbCapability();
            var repository = new TraceProjectionRepository(dataSource, dialect);
            var ingestion = new TraceIngestionService(
                new FdcTraceSource(new FdcCollectDataRepository(dataSource, dialect)),
                repository);

            var first = await ingestion.EnqueueAsync(100);
            var replay = await ingestion.EnqueueAsync(100);
            InsertSample(connectionString, "C-3", t0.AddSeconds(2), 13.75m);
            var equalTimestampContinuation = await ingestion.EnqueueAsync(100);

            first.Should().Be(2);
            replay.Should().Be(0);
            equalTimestampContinuation.Should().Be(1);
            ReadCollectIds(connectionString).Should().Equal("C-1", "C-2", "C-3");
            ReadCursor(connectionString).Should().Be("C-3",
                "the durable binding cursor must advance on the equal-timestamp tie-breaker");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void InsertSample(
        string connectionString,
        string collectId,
        DateTime collectedAt,
        decimal value)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FDC_COLLECT_DATA VALUES
                (@collectId, 'EQ-1', 'PARAM-1', @value, @collectedAt, 'Good', 0, 100);
            """;
        command.Parameters.AddWithValue("@collectId", collectId);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@collectedAt", collectedAt);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadCollectIds(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLLECT_ID FROM IVT_TRACE_PROJECTION_INBOX
            ORDER BY COLLECTED_AT, COLLECT_ID;
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static string ReadCursor(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LAST_COLLECT_ID FROM IVT_TRACE_INGESTION_CURSOR
            WHERE BINDING_ID = 'BIND-1';
            """;
        return (string)command.ExecuteScalar()!;
    }
}
