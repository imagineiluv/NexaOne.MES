using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Infrastructure;
using NexaOne.UnitTests.TestInfrastructure;

namespace NexaOne.UnitTests.Ivt;

public sealed class FdcTraceRetentionGuardTests
{
    [Fact]
    public async Task Low_watermark_uses_cursor_or_effective_from_for_every_active_binding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-ivt-retention-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        var effectiveWithoutCursor = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IVT_TRACE_CONSUMPTION_BINDING (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        EFFECTIVE_FROM TEXT NOT NULL,
                        IS_ACTIVE INTEGER NOT NULL);
                    CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        LAST_COLLECTED_AT TEXT NOT NULL);
                    INSERT INTO IVT_TRACE_CONSUMPTION_BINDING VALUES
                        ('CURSOR', @cursorEffective, 1),
                        ('NO-CURSOR', @withoutCursor, 1),
                        ('INACTIVE', @inactive, 0);
                    INSERT INTO IVT_TRACE_INGESTION_CURSOR VALUES
                        ('CURSOR', @cursorAt),
                        ('INACTIVE', @inactive);
                    """;
                command.Parameters.AddWithValue("@cursorEffective", effectiveWithoutCursor.AddDays(-10));
                command.Parameters.AddWithValue("@withoutCursor", effectiveWithoutCursor);
                command.Parameters.AddWithValue("@inactive", effectiveWithoutCursor.AddYears(-1));
                command.Parameters.AddWithValue("@cursorAt", effectiveWithoutCursor.AddDays(5));
                command.ExecuteNonQuery();
            }

            var guard = new FdcTraceRetentionGuard(new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            });

            (await guard.GetLowWatermarkAsync()).Should().Be(effectiveWithoutCursor);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE IVT_TRACE_CONSUMPTION_BINDING SET IS_ACTIVE=0;";
                command.ExecuteNonQuery();
            }
            (await guard.GetLowWatermarkAsync()).Should().BeNull();
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Low_watermark_uses_effective_from_when_a_durable_cursor_is_stale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-ivt-retention-stale-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        var effectiveFrom = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IVT_TRACE_CONSUMPTION_BINDING (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        EFFECTIVE_FROM TEXT NOT NULL,
                        IS_ACTIVE INTEGER NOT NULL);
                    CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        LAST_COLLECTED_AT TEXT NOT NULL);
                    INSERT INTO IVT_TRACE_CONSUMPTION_BINDING VALUES
                        ('STALE', @effectiveFrom, 1);
                    INSERT INTO IVT_TRACE_INGESTION_CURSOR VALUES
                        ('STALE', @staleCursor);
                    """;
                command.Parameters.AddWithValue("@effectiveFrom", effectiveFrom);
                command.Parameters.AddWithValue("@staleCursor", effectiveFrom.AddDays(-10));
                command.ExecuteNonQuery();
            }

            var guard = new FdcTraceRetentionGuard(new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            });

            (await guard.GetLowWatermarkAsync()).Should().Be(effectiveFrom,
                "retention and TRACE reads both resume from max(EffectiveFrom, cursor)");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00+10:00")]
    [InlineData("2026-01-01 00:00:00Z")]
    [InlineData("2026-02-30 00:00:00")]
    public async Task Low_watermark_rejects_noncanonical_active_binding_or_cursor_time(
        string unsafeTimestamp)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-ivt-retention-invalid-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IVT_TRACE_CONSUMPTION_BINDING (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        EFFECTIVE_FROM TEXT NOT NULL,
                        IS_ACTIVE INTEGER NOT NULL);
                    CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
                        BINDING_ID TEXT NOT NULL PRIMARY KEY,
                        LAST_COLLECTED_AT TEXT NOT NULL);
                    INSERT INTO IVT_TRACE_CONSUMPTION_BINDING VALUES
                        ('SAFE', '2026-01-01 00:00:00.0000000', 1),
                        ('UNSAFE', '2025-01-01 00:00:00.0000000', 1);
                    INSERT INTO IVT_TRACE_INGESTION_CURSOR VALUES
                        ('UNSAFE', @unsafeTimestamp);
                    """;
                command.Parameters.AddWithValue("@unsafeTimestamp", unsafeTimestamp);
                command.ExecuteNonQuery();
            }

            var guard = new FdcTraceRetentionGuard(new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            });

            var action = () => guard.GetLowWatermarkAsync();

            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*non-canonical UTC timestamp*UNSAFE*");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
