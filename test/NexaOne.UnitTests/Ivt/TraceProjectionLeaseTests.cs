using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaOne.IVT.Infrastructure;

namespace NexaOne.UnitTests.Ivt;

public sealed class TraceProjectionLeaseTests
{
    [Fact]
    public void Exact_sqlite_lease_primary_key_race_is_classified()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IVT_TRACE_PROJECTION_LEASE (
                BINDING_ID TEXT NOT NULL PRIMARY KEY);
            INSERT INTO IVT_TRACE_PROJECTION_LEASE (BINDING_ID) VALUES ('B1');
            """;
        command.ExecuteNonQuery();
        command.CommandText =
            "INSERT INTO IVT_TRACE_PROJECTION_LEASE (BINDING_ID) VALUES ('B1');";

        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        TraceProjectionRepository.IsLeaseIdentityRace(exception).Should().BeTrue();
    }

    [Fact]
    public void Arbitrary_database_failure_is_not_classified_as_a_lease_race()
        => TraceProjectionRepository.IsLeaseIdentityRace(
                new InjectedDbException("forced connection failure"))
            .Should().BeFalse();

    [Fact]
    public void Exact_sqlite_cursor_primary_key_race_is_classified()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IVT_TRACE_INGESTION_CURSOR (
                BINDING_ID TEXT NOT NULL PRIMARY KEY);
            INSERT INTO IVT_TRACE_INGESTION_CURSOR (BINDING_ID) VALUES ('B1');
            """;
        command.ExecuteNonQuery();
        command.CommandText =
            "INSERT INTO IVT_TRACE_INGESTION_CURSOR (BINDING_ID) VALUES ('B1');";

        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        TraceProjectionRepository.IsCursorIdentityRace(exception).Should().BeTrue();
    }

    [Fact]
    public void Arbitrary_database_failure_is_not_classified_as_a_cursor_race()
        => TraceProjectionRepository.IsCursorIdentityRace(
                new InjectedDbException("forced connection failure"))
            .Should().BeFalse();

    private sealed class InjectedDbException(string message) : DbException(message);
}
