using Microsoft.Data.Sqlite;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.UnitTests.TestInfrastructure;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcTraceSourceTests
{
    [Fact]
    public async Task ReadAsync_uses_a_stable_equal_timestamp_cursor_on_sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-trace-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        var t0 = new DateTime(2026, 8, 27, 1, 0, 0, DateTimeKind.Utc);
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
                    INSERT INTO FDC_COLLECT_DATA VALUES
                        ('C-0', 'EQ-1', 'PARAM-1', 10, @t0, 'Good', 0, 100),
                        ('C-1', 'EQ-1', 'PARAM-1', 11, @t1, 'Good', 0, 100),
                        ('C-2', 'EQ-1', 'PARAM-1', 12.345678, @t1, 'Good', 0, 100),
                        ('C-3', 'EQ-1', 'PARAM-1', 13, @t2, 'Good', 0, 100),
                        ('OTHER', 'EQ-2', 'PARAM-1', 99, @t2, 'Good', 0, 100);
                    """;
                command.Parameters.AddWithValue("@t0", t0);
                command.Parameters.AddWithValue("@t1", t0.AddSeconds(1));
                command.Parameters.AddWithValue("@t2", t0.AddSeconds(2));
                command.ExecuteNonQuery();
            }

            var dataSource = new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            };
            IFdcTraceSource source = new FdcTraceSource(
                new FdcCollectDataRepository(dataSource, new SqliteEesDbCapability()));

            var result = await source.ReadAsync(
            [
                new FdcTraceReadScope(
                    "BIND-1", "EQ-1", "PARAM-1", t0, null,
                    t0.AddSeconds(1), "C-1"),
            ], 10);

            result.Select(sample => sample.CollectId).Should().Equal("C-2", "C-3");
            result[0].Value.Should().Be(12.345678m);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task DeleteOlderThanAsync_uses_bounded_batches_and_leaves_newer_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-retention-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
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
                    CREATE INDEX IX_FDC_COLLECT_RETENTION
                        ON FDC_COLLECT_DATA (COLLECTED_AT, COLLECT_ID);
                    INSERT INTO FDC_COLLECT_DATA VALUES
                        ('OLD-1', 'EQ-1', 'P-1', 1, '2025-01-01 00:00:00', 'Good', 0, 100),
                        ('OLD-2', 'EQ-1', 'P-1', 2, '2025-01-01 00:00:00', 'Good', 0, 100),
                        ('OLD-3', 'EQ-1', 'P-1', 3, '2025-01-02 00:00:00', 'Good', 0, 100),
                        ('OLD-4', 'EQ-1', 'P-1', 4, '2025-01-03 00:00:00', 'Good', 0, 100),
                        ('OLD-5', 'EQ-1', 'P-1', 5, '2025-01-04 00:00:00', 'Good', 0, 100),
                        ('OLD-6', 'EQ-1', 'P-1', 6, '2025-01-05 00:00:00', 'Good', 0, 100),
                        ('NEW-1', 'EQ-1', 'P-1', 7, '2027-01-01 00:00:00', 'Good', 0, 100);
                    """;
                command.ExecuteNonQuery();
            }

            var dataSource = new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            };
            var repository = new FdcCollectDataRepository(
                dataSource,
                new SqliteEesDbCapability(),
                retentionBatchSize: 2,
                maxRetentionBatchesPerCall: 2);
            var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            (await repository.DeleteOlderThanAsync(cutoff)).Should().Be(4,
                "one invocation is capped at batch-size times maximum batches");
            (await repository.DeleteOlderThanAsync(cutoff)).Should().Be(2,
                "the next maintenance pass resumes the remaining indexed backlog");

            using var verify = new SqliteConnection(connectionString);
            verify.Open();
            using var count = verify.CreateCommand();
            count.CommandText = "SELECT GROUP_CONCAT(COLLECT_ID, ',') FROM FDC_COLLECT_DATA";
            Convert.ToString(count.ExecuteScalar()).Should().Be("NEW-1");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ReadAsync_returns_one_globally_ordered_bounded_stream_for_all_scopes()
    {
        var t0 = new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc);
        var repository = new Mock<IFdcCollectDataRepository>();
        repository
            .Setup(x => x.GetTraceAsync(
                "EQ-A", "PARAM-A", t0, null, null, null, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Sample("C-2", "EQ-A", "PARAM-A", 2m, t0.AddSeconds(2))]);
        repository
            .Setup(x => x.GetTraceAsync(
                "EQ-B", "PARAM-B", t0, null, null, null, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Sample("C-1", "EQ-B", "PARAM-B", 1m, t0.AddSeconds(1)),
                Sample("C-3", "EQ-B", "PARAM-B", 3m, t0.AddSeconds(3)),
            ]);
        IFdcTraceSource source = new FdcTraceSource(repository.Object);

        var result = await source.ReadAsync(
        [
            new FdcTraceReadScope("BIND-A", "EQ-A", "PARAM-A", t0, null, null, null),
            new FdcTraceReadScope("BIND-B", "EQ-B", "PARAM-B", t0, null, null, null),
        ], 2);

        result.Should().Equal(
            new FdcTraceSample("BIND-B", "C-1", "EQ-B", "PARAM-B", 1m, "Good", t0.AddSeconds(1)),
            new FdcTraceSample("BIND-A", "C-2", "EQ-A", "PARAM-A", 2m, "Good", t0.AddSeconds(2)));
    }

    private static FdcCollectData Sample(
        string id,
        string equipmentId,
        string parameterId,
        decimal value,
        DateTime collectedAt) =>
        FdcCollectData.Create(
            id, equipmentId, parameterId, value, collectedAt, "Good", 0m, 100m).Value;

}
