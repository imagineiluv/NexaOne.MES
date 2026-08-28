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
    public void Retention_diagnostics_are_additive_and_preserve_the_collect_repository_abi()
    {
        var legacyDelete = typeof(IFdcCollectDataRepository).GetMethod(
            nameof(IFdcCollectDataRepository.DeleteOlderThanAsync));

        legacyDelete.Should().NotBeNull();
        legacyDelete!.ReturnType.Should().Be<Task<int>>();
        typeof(FdcCollectDataRepository)
            .Should().Implement<IFdcCollectDataRetentionRepository>();
    }

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
                    CREATE TABLE FDC_TRACE_RETENTION_STATE (
                        STATE_ID TEXT NOT NULL PRIMARY KEY,
                        COMPLETENESS_BOUNDARY TEXT NOT NULL,
                        CREATED_BY TEXT NOT NULL,
                        CREATED_AT TEXT NOT NULL,
                        UPDATED_BY TEXT NOT NULL,
                        UPDATED_AT TEXT NOT NULL);
                    INSERT INTO FDC_TRACE_RETENTION_STATE VALUES
                        ('GLOBAL', '2020-01-01', 'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
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
    public async Task Sqlite_TRACE_order_matches_the_normalized_equal_timestamp_cursor_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-trace-order-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        var at = new DateTime(2026, 8, 27, 1, 0, 0, 100, DateTimeKind.Utc);
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
                        ('Z-LAST', 'EQ-1', 'PARAM-1', 1,
                         '2026-08-27 01:00:00.1', 'Good', 0, 100),
                        ('A-FIRST', 'EQ-1', 'PARAM-1', 2,
                         '2026-08-27 01:00:00.10', 'Good', 0, 100);
                    """;
                command.ExecuteNonQuery();
            }

            var dataSource = new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            };
            var repository = new FdcCollectDataRepository(dataSource, new SqliteEesDbCapability());

            var first = await repository.GetTraceAsync(
                "EQ-1", "PARAM-1", at.AddMinutes(-1), null, null, null, 1);
            var second = await repository.GetTraceAsync(
                "EQ-1", "PARAM-1", at.AddMinutes(-1), null,
                first.Single().CollectedAt, first.Single().Id, 1);

            first.Single().Id.Should().Be("A-FIRST");
            second.Single().Id.Should().Be("Z-LAST");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task PurgeOlderThanAsync_uses_bounded_batches_and_leaves_newer_samples()
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
                    CREATE TABLE FDC_TRACE_RETENTION_STATE (
                        STATE_ID TEXT NOT NULL PRIMARY KEY,
                        COMPLETENESS_BOUNDARY TEXT NOT NULL,
                        CREATED_BY TEXT NOT NULL,
                        CREATED_AT TEXT NOT NULL,
                        UPDATED_BY TEXT NOT NULL,
                        UPDATED_AT TEXT NOT NULL);
                    INSERT INTO FDC_TRACE_RETENTION_STATE VALUES
                        ('GLOBAL', '2020-01-01', 'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                    CREATE INDEX IX_FDC_COLLECT_RETENTION
                        ON FDC_COLLECT_DATA (COLLECTED_AT, COLLECT_ID);
                    INSERT INTO FDC_COLLECT_DATA VALUES
                        ('OLD-1', 'EQ-1', 'P-1', 1, '2025-01-01 00:00:00', 'Good', 0, 100),
                        ('OLD-2', 'EQ-1', 'P-1', 2, '2025-01-01 00:00:00', 'Good', 0, 100),
                        ('OLD-3', 'EQ-1', 'P-1', 3, '2025-01-02 00:00:00', 'Good', 0, 100),
                        ('OLD-4', 'EQ-1', 'P-1', 4, '2025-01-03 00:00:00', 'Good', 0, 100),
                        ('OLD-5', 'EQ-1', 'P-1', 5, '2025-01-04 00:00:00', 'Good', 0, 100),
                        ('OLD-6', 'EQ-1', 'P-1', 6, '2025-01-05 00:00:00', 'Good', 0, 100),
                        ('BOUNDARY', 'EQ-1', 'P-1', 6.5, '2026-01-01 00:00:00.0001234', 'Good', 0, 100),
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
            var retentionRepository = (IFdcCollectDataRetentionRepository)repository;
            var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234);

#pragma warning disable CS0618 // Reflection/ABI contract: legacy entry point must remain but fail closed.
            var legacyDelete = () => repository.DeleteOlderThanAsync(cutoff);
#pragma warning restore CS0618
            await legacyDelete.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*cannot enforce the IVT low-watermark guard*");
            (await repository.GetTraceRetentionStateAsync()).CompletenessBoundary
                .Should().Be(new DateTime(2020, 1, 1),
                    "the disabled legacy API must neither advance state nor delete TRACE rows");

            var first = await retentionRepository.PurgeOlderThanAsync(cutoff);
            first.DeletedRows.Should().Be(4,
                "one invocation is capped at batch-size times maximum batches");
            first.BatchLimitReached.Should().BeTrue();
            first.OldestRemainingCollectedAt.Should().Be(new DateTime(2025, 1, 4));
            first.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
            (await repository.GetTraceRetentionStateAsync()).CompletenessBoundary
                .Should().Be(cutoff, "the durable gap boundary must advance before deletion commits");

            var second = await retentionRepository.PurgeOlderThanAsync(cutoff);
            second.DeletedRows.Should().Be(2,
                "the next maintenance pass resumes the remaining indexed backlog");
            second.BatchLimitReached.Should().BeFalse();
            second.OldestRemainingCollectedAt.Should().BeNull();

            var staleScopeRead = () => new FdcTraceSource(repository).ReadAsync(
                [new FdcTraceReadScope("FUTURE-BINDING", "EQ-1", "P-1", cutoff.AddDays(-1), null, null, null)],
                10);
            await staleScopeRead.Should().ThrowAsync<FdcTraceGapException>()
                .WithMessage("*FUTURE-BINDING*completeness boundary*");

            using var verify = new SqliteConnection(connectionString);
            verify.Open();
            using var count = verify.CreateCommand();
            count.CommandText = """
                SELECT GROUP_CONCAT(COLLECT_ID, ',')
                  FROM (SELECT COLLECT_ID FROM FDC_COLLECT_DATA ORDER BY COLLECTED_AT, COLLECT_ID)
                """;
            Convert.ToString(count.ExecuteScalar()).Should().Be("BOUNDARY,NEW-1",
                "retention uses strict '<' and must preserve a sample exactly at the full-precision boundary");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Purge_rolls_back_the_completeness_boundary_when_the_delete_transaction_fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-retention-atomic-{Guid.NewGuid():N}.db");
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
                    CREATE TABLE FDC_TRACE_RETENTION_STATE (
                        STATE_ID TEXT NOT NULL PRIMARY KEY,
                        COMPLETENESS_BOUNDARY TEXT NOT NULL,
                        CREATED_BY TEXT NOT NULL,
                        CREATED_AT TEXT NOT NULL,
                        UPDATED_BY TEXT NOT NULL,
                        UPDATED_AT TEXT NOT NULL);
                    INSERT INTO FDC_TRACE_RETENTION_STATE VALUES
                        ('GLOBAL', '2020-01-01', 'SYSTEM', CURRENT_TIMESTAMP, 'SYSTEM', CURRENT_TIMESTAMP);
                    INSERT INTO FDC_COLLECT_DATA VALUES
                        ('OLD', 'EQ-1', 'P-1', 1, '2025-01-01', 'Good', 0, 100);
                    CREATE TRIGGER TR_TEST_ABORT_FDC_PURGE
                    BEFORE DELETE ON FDC_COLLECT_DATA
                    BEGIN
                        SELECT RAISE(ABORT, 'forced purge failure');
                    END;
                    """;
                command.ExecuteNonQuery();
            }
            var dataSource = new EesDataSource
            {
                Provider = new SqliteTestDatabaseProvider(),
                ConnectionString = connectionString,
            };
            var repository = new FdcCollectDataRepository(dataSource, new SqliteEesDbCapability());
            var retentionRepository = (IFdcCollectDataRetentionRepository)repository;

            var act = () => retentionRepository.PurgeOlderThanAsync(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            await act.Should().ThrowAsync<SqliteException>()
                .WithMessage("*forced purge failure*");
            using var verify = new SqliteConnection(connectionString);
            verify.Open();
            using (var boundary = verify.CreateCommand())
            {
                boundary.CommandText = "SELECT COMPLETENESS_BOUNDARY FROM FDC_TRACE_RETENTION_STATE";
                Convert.ToString(boundary.ExecuteScalar()).Should().Be("2020-01-01",
                    "boundary advance and DELETE must roll back as one transaction");
            }
            using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM FDC_COLLECT_DATA";
            Convert.ToInt64(count.ExecuteScalar()).Should().Be(1);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(false, "GLOBAL row is missing")]
    [InlineData(true, "GLOBAL boundary is missing")]
    public async Task Retention_state_read_fails_closed_when_the_singleton_or_boundary_is_missing(
        bool insertNullBoundary,
        string expectedMessage)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexaone-fdc-retention-state-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE FDC_TRACE_RETENTION_STATE (
                        STATE_ID TEXT NOT NULL PRIMARY KEY,
                        COMPLETENESS_BOUNDARY TEXT NULL);
                    """;
                command.ExecuteNonQuery();
                if (insertNullBoundary)
                {
                    command.CommandText = """
                        INSERT INTO FDC_TRACE_RETENTION_STATE
                            (STATE_ID, COMPLETENESS_BOUNDARY)
                        VALUES ('GLOBAL', NULL);
                        """;
                    command.ExecuteNonQuery();
                }
            }

            var repository = new FdcCollectDataRepository(
                new EesDataSource
                {
                    Provider = new SqliteTestDatabaseProvider(),
                    ConnectionString = connectionString,
                },
                new SqliteEesDbCapability());

            var act = () => repository.GetTraceRetentionStateAsync();

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{expectedMessage}*");
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
        repository.As<IFdcTraceRetentionStateRepository>()
            .Setup(x => x.GetTraceRetentionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcTraceRetentionState(DateTime.UnixEpoch));
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

    [Fact]
    public async Task ReadAsync_throws_an_explicit_gap_when_retention_advanced_before_or_during_read()
    {
        var effectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boundary = effectiveFrom.AddDays(1);
        var repository = new Mock<IFdcCollectDataRepository>();
        repository.Setup(x => x.GetTraceAsync(
                "EQ-1", "PARAM-1", effectiveFrom, null, null, null, 10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.As<IFdcTraceRetentionStateRepository>()
            .SetupSequence(x => x.GetTraceRetentionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcTraceRetentionState(DateTime.UnixEpoch))
            .ReturnsAsync(new FdcTraceRetentionState(boundary));
        var source = new FdcTraceSource(repository.Object);

        var act = () => source.ReadAsync(
            [new FdcTraceReadScope("BIND-1", "EQ-1", "PARAM-1", effectiveFrom, null, null, null)],
            10);

        var exception = await act.Should().ThrowAsync<FdcTraceGapException>();
        exception.Which.ScopeId.Should().Be("BIND-1");
        exception.Which.RequestedFrom.Should().Be(effectiveFrom);
        exception.Which.CompletenessBoundary.Should().Be(boundary);
    }

    [Fact]
    public async Task ReadAsync_uses_effective_from_when_it_is_later_than_a_stale_cursor_for_gap_detection()
    {
        var staleCursor = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boundary = staleCursor.AddDays(4);
        var effectiveFrom = staleCursor.AddDays(9);
        var repository = new Mock<IFdcCollectDataRepository>();
        repository.Setup(x => x.GetTraceAsync(
                "EQ-1", "PARAM-1", effectiveFrom, null, staleCursor, "C-OLD", 10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.As<IFdcTraceRetentionStateRepository>()
            .Setup(x => x.GetTraceRetentionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcTraceRetentionState(boundary));
        var source = new FdcTraceSource(repository.Object);

        var result = await source.ReadAsync(
            [new FdcTraceReadScope(
                "BIND-1", "EQ-1", "PARAM-1", effectiveFrom, null, staleCursor, "C-OLD")],
            10);

        result.Should().BeEmpty(
            "the repository and completeness check both resume from max(EffectiveFrom, cursor)");
    }

    [Fact]
    public async Task ReadAsync_normalizes_local_scope_times_before_gap_detection_and_repository_access()
    {
        var boundary = new DateTime(2026, 8, 28, 3, 30, 0, DateTimeKind.Utc);
        var requestedUtc = boundary.AddMinutes(-30);
        var requestedLocal = requestedUtc.ToLocalTime();
        var repository = new Mock<IFdcCollectDataRepository>();
        repository.As<IFdcTraceRetentionStateRepository>()
            .Setup(x => x.GetTraceRetentionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcTraceRetentionState(boundary));
        var source = new FdcTraceSource(repository.Object);

        var act = () => source.ReadAsync(
            [new FdcTraceReadScope(
                "LOCAL-BINDING", "EQ-1", "PARAM-1", requestedLocal, null, null, null)],
            10);

        var exception = await act.Should().ThrowAsync<FdcTraceGapException>();
        exception.Which.RequestedFrom.Should().Be(requestedUtc);
        exception.Which.RequestedFrom.Kind.Should().Be(DateTimeKind.Utc);
        repository.Verify(x => x.GetTraceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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
