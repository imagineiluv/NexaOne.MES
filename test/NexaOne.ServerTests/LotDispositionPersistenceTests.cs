using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Infrastructure;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class LotDispositionPersistenceTests :
    IClassFixture<PomLotPersistenceTests.LotFactory>
{
    private readonly PomLotPersistenceTests.LotFactory _factory;

    public LotDispositionPersistenceTests(PomLotPersistenceTests.LotFactory factory)
        => _factory = factory;

    [Fact]
    public async Task Persists_authenticated_actor_and_prevents_over_allocation()
    {
        var (lotId, executionId) = SeedDefectLot();
        var repository = new LotDispositionRepository(DataSource());
        var scope = await repository.GetScopeAsync(
            "PLANT01", lotId, null, "CUT", executionId, "SCRATCH");
        scope.Should().NotBeNull();
        scope!.DefectExecutionId.Should().Be(executionId);
        scope.DefectCode.Should().Be("SCRATCH");
        scope.AvailableQuantity.Should().Be(3m);
        var service = new LotDispositionService(repository);
        var firstCommand = Command(lotId, executionId, "DISP:1", 2m);

        var first = await service.RecordAsync(firstCommand);
        var replay = await service.RecordAsync(firstCommand);
        var over = await service.RecordAsync(Command(lotId, executionId, "DISP:2", 2m));

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.DispositionId.Should().Be(first.Value.DispositionId);
        over.IsFailure.Should().BeTrue();
        over.Error.Code.Should().Be("POM.LotDisposition.QuantityExceeded");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(1);
        Scalar<string>("SELECT DECIDED_BY FROM POM_LOT_DISPOSITION WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be("operator01");
        Scalar<string>("SELECT CLIENT_CHANNEL FROM POM_LOT_DISPOSITION WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be("MOBILE");
    }

    [Fact]
    public async Task Code_only_disposition_requires_evidence_from_the_requested_process()
    {
        var (lotId, _) = SeedDefectLot();
        var service = new LotDispositionService(new LotDispositionRepository(DataSource()));
        var command = Command(lotId, "unused", "DISP:PROCESS:" + lotId, 1m) with
        {
            ProcessId = "POLISH",
            DefectExecutionId = null,
        };

        var result = await service.RecordAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("POM.LotDisposition.ScopeNotFound");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(0);
    }

    [Fact]
    public void Database_rejects_defect_evidence_owned_by_a_different_lot()
    {
        var (_, evidenceExecutionId) = SeedDefectLot();
        var (differentLotId, _) = SeedDefectLot();
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var insert = () => ExecWithForeignKeys("""
            INSERT INTO POM_LOT_DISPOSITION
              (DISPOSITION_ID, PLANT_ID, LOT_ID, DEFECT_EXECUTION_ID, DEFECT_CODE,
               DISPOSITION_TYPE, QUANTITY, REASON, DECIDED_BY, DECIDED_AT,
               IDEMPOTENCY_KEY, REQUEST_HASH, CLIENT_CHANNEL, CREATED_AT)
            VALUES (@id, 'PLANT01', @lot, @execution, 'SCRATCH',
                    'Scrap', 1, 'cross-lot evidence', 'operator01', @now,
                    @key, 'TEST-HASH', 'MES', @now);
            """, ("@id", suffix), ("@lot", differentLotId),
            ("@execution", evidenceExecutionId), ("@now", now), ("@key", "DISP:FK:" + suffix));

        insert.Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task Concurrent_allocations_allow_exactly_one_and_rollback_loser_guard()
    {
        var (lotId, executionId) = SeedDefectLot();
        var firstRepository = new LotDispositionRepository(DataSource());
        var secondRepository = new LotDispositionRepository(DataSource());
        var firstRecord = Record(lotId, executionId, "DISP:RACE:A:" + lotId, 2m,
            DateTime.UtcNow.AddSeconds(1));
        var secondRecord = Record(lotId, executionId, "DISP:RACE:B:" + lotId, 2m,
            DateTime.UtcNow.AddSeconds(2));
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = StartTogether(firstRepository, firstRecord, gate.Task);
        var secondTask = StartTogether(secondRepository, secondRecord, gate.Task);
        gate.SetResult(true);
        var results = await Task.WhenAll(firstTask, secondTask);

        results.Count(result => result).Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(1);
        Scalar<decimal>("SELECT SUM(QUANTITY) FROM POM_LOT_DISPOSITION WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(2m);
        var winner = results[0] ? firstRecord : secondRecord;
        var lotUpdatedAt = DateTime.Parse(
            Scalar<string>("SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lotId)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        lotUpdatedAt.Should().BeCloseTo(winner.DecidedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Concurrent_same_idempotency_key_replays_a_single_disposition()
    {
        var (lotId, executionId) = SeedDefectLot();
        var firstService = new LotDispositionService(new LotDispositionRepository(DataSource()));
        var secondService = new LotDispositionService(new LotDispositionRepository(DataSource()));
        var command = Command(lotId, executionId, "DISP:SAME:" + lotId, 2m);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = StartTogether(firstService, command, gate.Task);
        var secondTask = StartTogether(secondService, command, gate.Task);
        gate.SetResult(true);
        var results = await Task.WhenAll(firstTask, secondTask);

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.DispositionId).Distinct().Should().ContainSingle();
        Scalar<long>(
                "SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE IDEMPOTENCY_KEY=@key",
                ("@key", command.IdempotencyKey))
            .Should().Be(1);
    }

    [Fact]
    public async Task Insert_failure_rolls_back_lot_touch_and_disposition()
    {
        var (lotId, executionId) = SeedDefectLot();
        const string sentinel = "2001-02-03 04:05:06";
        var key = "DISP:ROLLBACK:" + lotId;
        Exec("UPDATE POM_LOT SET UPDATED_AT=@sentinel WHERE LOT_ID=@lot",
            ("@sentinel", sentinel), ("@lot", lotId));
        Exec($"""
            CREATE TRIGGER TRG_TEST_LOT_DISPOSITION_ABORT
            BEFORE INSERT ON POM_LOT_DISPOSITION
            WHEN NEW.IDEMPOTENCY_KEY = '{key}'
            BEGIN
              SELECT RAISE(ABORT, 'forced disposition failure');
            END;
            """);

        try
        {
            var repository = new LotDispositionRepository(DataSource());
            var record = Record(lotId, executionId, key, 1m, DateTime.UtcNow);
            Func<Task> insert = async () => _ = await repository.TryAddAsync(record);

            await insert.Should().ThrowAsync<SqliteException>();
            Scalar<long>(
                    "SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE IDEMPOTENCY_KEY=@key",
                    ("@key", key))
                .Should().Be(0);
            Scalar<string>("SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
                .Should().Be(sentinel);
        }
        finally
        {
            Exec("DROP TRIGGER IF EXISTS TRG_TEST_LOT_DISPOSITION_ABORT");
        }
    }

    private (string LotId, string ExecutionId) SeedDefectLot()
    {
        _ = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lot = $"DL_{suffix}";
        var execution = $"DX_{suffix}";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Exec("""
            INSERT INTO POM_LOT
              (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY,
               LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD,
               VERSION_NO, CONTROL_MODE, CREATED_BY, CREATED_AT)
            VALUES (@lot, 'PLANT01', NULL, 'ITEM01', 10, 3,
                    'Completed', 'Idle', 'CUT', 0, 'N', 1, 'Strict', 'TEST', @now);
            INSERT INTO POM_LOT_EXECUTION
              (EXECUTION_ID, LOT_ID, ACTION, IDEMPOTENCY_KEY, REQUEST_HASH,
               EXPECTED_VERSION, RESULT_VERSION, CONTROL_MODE, CLIENT_CHANNEL,
               CREATED_BY, CREATED_AT)
            VALUES (@execution, @lot, 'TrackOut', @execution, 'TEST-HASH',
                    1, 2, 'Strict', 'MES', 'operator01', @now);
            INSERT INTO POM_LOT_DEFECT_EXECUTION
              (EXECUTION_ID, LOT_ID, PLANT_ID, PROCESS_ID, DEFECT_CODE, DEFECT_QTY,
               EXECUTION_USER, CLIENT_CHANNEL, OCCURRED_AT, CREATED_AT)
            VALUES (@execution, @lot, 'PLANT01', 'CUT', 'SCRATCH', 3,
                    'operator01', 'MES', @now, @now);
            """, ("@lot", lot), ("@execution", execution), ("@now", now));
        return (lot, execution);
    }

    private EesDataSource DataSource()
    {
        _ = _factory.CreateClient();
        return new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString,
        };
    }

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void ExecWithForeignKeys(string sql, params (string Name, object? Value)[] parameters)
    {
        var connectionString = _factory.ConnString.Replace(
            "Foreign Keys=False", "Foreign Keys=True", StringComparison.OrdinalIgnoreCase);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> StartTogether(
        LotDispositionRepository repository,
        LotDispositionRecord record,
        Task gate)
    {
        await gate;
        return await repository.TryAddAsync(record);
    }

    private static async Task<Result<LotDispositionRecord>> StartTogether(
        LotDispositionService service,
        LotDispositionCommand command,
        Task gate)
    {
        await gate;
        return await service.RecordAsync(command);
    }

    private static LotDispositionRecord Record(
        string lotId,
        string executionId,
        string idempotencyKey,
        decimal quantity,
        DateTime decidedAt) => new(
        Guid.NewGuid().ToString("N"), "PLANT01", lotId, null, "CUT",
        executionId, "SCRATCH", "Scrap", quantity, "QUALITY", "confirmed defect",
        "operator01", decidedAt, executionId, idempotencyKey, "TEST-HASH",
        "MOBILE", "PDA-01");

    private static LotDispositionCommand Command(
        string lotId,
        string executionId,
        string idempotencyKey,
        decimal quantity) => new(
        "PLANT01", lotId, null, "CUT", executionId, "SCRATCH",
        "Scrap", quantity, "QUALITY", "confirmed defect", "operator01",
        idempotencyKey, "MOBILE", "PDA-01", executionId);
}
