using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.Server.Gateway;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class IvtTraceProjectionPersistenceTests
    : IClassFixture<IvtTraceProjectionPersistenceTests.TraceFactory>
{
    private readonly TraceFactory _factory;

    public IvtTraceProjectionPersistenceTests(TraceFactory factory) => _factory = factory;

    public sealed class TraceFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-ivt-trace-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "ivt-trace-integration-secret-key-32bytes!!!!");
            builder.UseSetting("Jwt:Issuer", "ivt-trace-test");
            builder.UseSetting("Jwt:Audience", "ivt-trace-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }

    [Fact]
    public async Task Persisted_counter_samples_project_once_and_survive_repoll()
    {
        var ids = SeedCounterTrace();
        var worker = BuildWorker();

        var first = await worker.ProjectBatchAsync();
        _ = await worker.ProjectBatchAsync();
        var inboxSummary = Scalar<string>(
            @"SELECT GROUP_CONCAT(STATUS || ':' || COALESCE(LAST_ERROR, ''), '|')
              FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id",
            ("@id", ids.BindingId));

        first.Should().BeGreaterThanOrEqualTo(2,
            $"this binding has one terminal baseline and one applied sample; the shared worker may also process " +
            $"other bindings seeded by parallel integration cases; inbox={inboxSummary}");
        Scalar<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@id", ("@id", ids.LotId))
            .Should().Be(96.5m);
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE MATERIAL_LOT_ID=@id",
            ("@id", ids.LotId)).Should().Be(1);
        Scalar<decimal>(
            "SELECT QUANTITY FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE MATERIAL_LOT_ID=@id",
            ("@id", ids.LotId)).Should().Be(3.5m);
        Scalar<string>(
            "SELECT SOURCE_EVENT_ID FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE MATERIAL_LOT_ID=@id",
            ("@id", ids.LotId)).Should().Be(ids.SecondCollectId);
        Scalar<string>(
            "SELECT CONSUMPTION_MODE FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE MATERIAL_LOT_ID=@id",
            ("@id", ids.LotId)).Should().Be("Trace");
        Scalar<string>(
            "SELECT OPERATOR_ID FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE MATERIAL_LOT_ID=@id",
            ("@id", ids.LotId)).Should().Be("operator",
                "TRACE consumption must retain the authenticated actor who mounted the material");
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE LOT_ID=@id AND TX_TYPE='Consumption'",
            ("@id", ids.LotId)).Should().Be(1);
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id AND STATUS='Ignored'",
            ("@id", ids.BindingId)).Should().Be(1);
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id AND STATUS='Applied'",
            ("@id", ids.BindingId)).Should().Be(1);
        Scalar<decimal>(
            "SELECT LAST_VALUE FROM IVT_TRACE_PROJECTION_STATE WHERE BINDING_ID=@id",
            ("@id", ids.BindingId)).Should().Be(13.5m);
        Scalar<string>(
            "SELECT LAST_COLLECT_ID FROM IVT_TRACE_PROJECTION_STATE WHERE BINDING_ID=@id",
            ("@id", ids.BindingId)).Should().Be(ids.SecondCollectId);
        Scalar<string>(
            "SELECT LAST_COLLECT_ID FROM IVT_TRACE_INGESTION_CURSOR WHERE BINDING_ID=@id",
            ("@id", ids.BindingId)).Should().Be(ids.SecondCollectId);
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id AND IS_WORK_ITEM=1",
            ("@id", ids.BindingId)).Should().Be(0,
                "terminal TRACE evidence must leave the filtered retry work set");
    }

    [Fact]
    public async Task Missing_feed_session_records_error_and_blocks_later_sample_for_binding()
    {
        var ids = SeedCounterTrace(includeFeedSession: false);
        var worker = BuildWorker();

        var completed = await worker.ProjectBatchAsync();

        completed.Should().Be(0);
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id AND STATUS='Error'",
            ("@id", ids.BindingId)).Should().Be(1, "the earliest row identifies the missing accounting context");
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id AND STATUS='Pending'",
            ("@id", ids.BindingId)).Should().Be(1, "later rows remain unprocessed to preserve counter order");
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_INBOX WHERE BINDING_ID=@id AND IS_WORK_ITEM=1",
            ("@id", ids.BindingId)).Should().Be(2,
                "error and pending rows must remain visible to the filtered retry queue");
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_CONSUMPTION_HISTORY WHERE MATERIAL_LOT_ID=@id",
            ("@id", ids.LotId)).Should().Be(0);
        Scalar<long>(
            "SELECT COUNT(*) FROM IVT_TRACE_PROJECTION_STATE WHERE BINDING_ID=@id",
            ("@id", ids.BindingId)).Should().Be(0);
    }

    [Fact]
    public async Task Binding_lease_allows_only_one_repository_to_claim_ordered_rows()
    {
        var ids = SeedCounterTrace();
        _ = _factory.CreateClient();
        var dataSource = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
        var firstRepository = new TraceProjectionRepository(dataSource, new SqliteEesDbCapability());
        var secondRepository = new TraceProjectionRepository(dataSource, new SqliteEesDbCapability());
        var traceSource = new FdcTraceSource(
            new FdcCollectDataRepository(dataSource, new SqliteEesDbCapability()));
        await new TraceIngestionService(traceSource, firstRepository).EnqueueAsync(100);

        var claims = await Task.WhenAll(
            firstRepository.GetPendingAsync(100),
            secondRepository.GetPendingAsync(100));
        var targetRows = claims.SelectMany(rows => rows)
            .Where(row => row.BindingId == ids.BindingId)
            .ToList();

        targetRows.Should().HaveCount(2);
        targetRows.Select(row => row.LeaseOwnerId).Distinct().Should().ContainSingle();
        claims.Count(rows => rows.Any(row => row.BindingId == ids.BindingId)).Should().Be(1);

        foreach (var lease in claims.SelectMany(rows => rows)
                     .Where(row => row.LeaseOwnerId is not null)
                     .Select(row => (row.BindingId, LeaseOwnerId: row.LeaseOwnerId!))
                     .Distinct())
        {
            await firstRepository.ReleaseLeaseAsync(lease.BindingId, lease.LeaseOwnerId);
        }
    }

    private TraceMaterialConsumptionWorker BuildWorker()
    {
        _ = _factory.CreateClient();
        var dataSource = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
        var repository = new TraceProjectionRepository(dataSource, new SqliteEesDbCapability());
        var traceSource = new FdcTraceSource(
            new FdcCollectDataRepository(dataSource, new SqliteEesDbCapability()));
        return new TraceMaterialConsumptionWorker(
            new TraceIngestionService(traceSource, repository),
            repository,
            new ConsumptionService(new ConsumptionRepository(dataSource)),
            enabled: true,
            pollIntervalSeconds: 1,
            batchSize: 100);
    }

    private SeedIds SeedCounterTrace(bool includeFeedSession = true)
    {
        _ = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var bindingId = $"B_{suffix}";
        var parameterId = $"P_{suffix}";
        var equipmentId = $"E_{suffix}";
        var lotId = $"L_{suffix}";
        var feedId = $"F_{suffix}";
        var firstCollectId = $"C1_{suffix}";
        var secondCollectId = $"C2_{suffix}";
        // V150 initializes an empty raw TRACE store as provably complete only from its durable
        // boundary forward. Seed this ingestion fixture after that boundary; inserting an older
        // binding/sample would correctly exercise the explicit late-arrival gap path instead of
        // the normal projection path covered by these tests.
        var completenessBoundary = DateTime.Parse(
            Scalar<string>(
                "SELECT COMPLETENESS_BOUNDARY FROM FDC_TRACE_RETENTION_STATE WHERE STATE_ID='GLOBAL'"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var effectiveFrom = completenessBoundary.AddMilliseconds(1);
        var firstAt = effectiveFrom.AddSeconds(1);
        var secondAt = firstAt.AddSeconds(1);

        Exec("""
            INSERT INTO IVT_MATERIAL_LOT
              (LOT_ID, MATERIAL_ID, LOT_NO, WAREHOUSE, CURRENT_QTY, UNIT, STATUS,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@lot, @material, @lot, 'LINE', 100, 'kg', 'InStock',
                    'TEST', @now, 'TEST', @now);

            INSERT INTO FDC_PARAMETER
              (PARAMETER_ID, PARAMETER_NAME, EQUIPMENT_ID, UNIT, LOWER_LIMIT, UPPER_LIMIT,
               IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@parameter, @parameter, @equipment, 'kg', 0, 999999, 1,
                    'TEST', @now, 'TEST', @now);

            INSERT INTO IVT_TRACE_CONSUMPTION_BINDING
              (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
               CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT, EFFECTIVE_FROM,
               IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@binding, 'PLANT01', @equipment, @parameter, 'FEED01',
                    'CounterDelta', 1, NULL, 'kg', @effectiveFrom, 1,
                    'TEST', @now, 'TEST', @now);

            INSERT INTO FDC_COLLECT_DATA
              (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY,
               LOWER_LIMIT, UPPER_LIMIT)
            VALUES (@firstCollect, @equipment, @parameter, 10, @firstAt, 'Good', 0, 999999);

            INSERT INTO FDC_COLLECT_DATA
              (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY,
               LOWER_LIMIT, UPPER_LIMIT)
            VALUES (@secondCollect, @equipment, @parameter, 13.5, @secondAt, 'Good', 0, 999999);
            """,
            ("@lot", lotId), ("@material", $"M_{suffix}"), ("@parameter", parameterId),
            ("@equipment", equipmentId), ("@binding", bindingId),
            ("@effectiveFrom", DbDate(effectiveFrom)),
            ("@firstCollect", firstCollectId), ("@secondCollect", secondCollectId),
            ("@firstAt", DbDate(firstAt)), ("@secondAt", DbDate(secondAt)),
            ("@now", DbDate(DateTime.UtcNow)));

        if (includeFeedSession)
        {
            Exec("""
                INSERT INTO IVT_MATERIAL_FEED_SESSION
                  (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID, MATERIAL_LOT_ID,
                   MATERIAL_ID, MOUNTED_AT, MOUNTED_BY, STATUS,
                   CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@feed, 'PLANT01', @equipment, 'FEED01', @lot, @material,
                        @mountedAt, 'operator', 'Mounted', 'TEST', @now, 'TEST', @now);
                """,
                ("@feed", feedId), ("@equipment", equipmentId), ("@lot", lotId),
                ("@material", $"M_{suffix}"), ("@mountedAt", DbDate(effectiveFrom)),
                ("@now", DbDate(DateTime.UtcNow)));
        }

        return new SeedIds(bindingId, lotId, firstCollectId, secondCollectId);
    }

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string DbDate(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private sealed record SeedIds(
        string BindingId,
        string LotId,
        string FirstCollectId,
        string SecondCollectId);
}
