using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MaterialLotPersistenceTests :
    IClassFixture<IvtTraceProjectionPersistenceTests.TraceFactory>
{
    private readonly IvtTraceProjectionPersistenceTests.TraceFactory _factory;

    public MaterialLotPersistenceTests(IvtTraceProjectionPersistenceTests.TraceFactory factory)
        => _factory = factory;

    [Fact]
    public async Task Lifecycle_and_consumption_share_balance_status_version_and_one_tx_ledger()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"ML_{suffix}";
        var service = Service();

        (await service.ExecuteAsync(Command(lotId, "Receive", 0, suffix) with
        {
            MaterialId = $"MAT_{suffix}", LotNumber = $"SUP_{suffix}", Quantity = 10m,
            Unit = "kg", Location = "STORE",
        })).IsSuccess.Should().BeTrue();
        (await service.ExecuteAsync(Command(lotId, "Move", 1, suffix) with
        {
            TransactionId = $"MOVE_{suffix}", IdempotencyKey = $"MOVE:{suffix}",
            SourceEventId = $"MOVE:{suffix}", Location = "LINE-01",
        })).IsSuccess.Should().BeTrue();
        (await service.ExecuteAsync(Command(lotId, "Hold", 2, suffix) with
        {
            TransactionId = $"HOLD_{suffix}", IdempotencyKey = $"HOLD:{suffix}",
            SourceEventId = $"HOLD:{suffix}", Reason = "incoming inspection",
        })).IsSuccess.Should().BeTrue();

        var consumption = new ConsumptionService(new ConsumptionRepository(DataSource()));
        var blocked = await consumption.ConsumeAsync(Consumption(lotId, suffix, "BLOCKED", 1m));
        blocked.IsFailure.Should().BeTrue("held stock cannot be consumed");
        Scalar<decimal>("SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(10m);

        (await service.ExecuteAsync(Command(lotId, "Release", 3, suffix) with
        {
            TransactionId = $"REL_{suffix}", IdempotencyKey = $"REL:{suffix}",
            SourceEventId = $"REL:{suffix}",
        })).IsSuccess.Should().BeTrue();
        (await consumption.ConsumeAsync(Consumption(lotId, suffix, "USED", 2m)))
            .IsSuccess.Should().BeTrue();

        var stale = await service.ExecuteAsync(Command(lotId, "Adjustment", 4, suffix) with
        {
            TransactionId = $"STALE_{suffix}", IdempotencyKey = $"STALE:{suffix}",
            SourceEventId = $"STALE:{suffix}", Quantity = 1m, Reason = "cycle count",
        });
        stale.IsFailure.Should().BeTrue("consumption increments the same LOT version");
        var adjust = Command(lotId, "Adjustment", 5, suffix) with
        {
            TransactionId = $"ADJ_{suffix}", IdempotencyKey = $"ADJ:{suffix}",
            SourceEventId = $"ADJ:{suffix}", Quantity = 1m, Reason = "cycle count",
        };
        var applied = await service.ExecuteAsync(adjust);
        var replay = await service.ExecuteAsync(adjust);

        applied.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        Scalar<decimal>("SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(9m);
        Scalar<long>("SELECT VERSION_NO FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(6);
        Scalar<string>("SELECT STATUS FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be("InStock");
        Scalar<long>("SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(6, "Receive/Move/Hold/Release/Consumption/Adjustment share IVT_MATERIAL_TX");
        Scalar<long>("SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE LOT_ID=@lot AND REQUEST_HASH IS NOT NULL",
            ("@lot", lotId)).Should().Be(6);
    }

    [Fact]
    public async Task Same_expected_version_allows_only_one_competing_balance_change()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"RACE_{suffix}";
        var service = Service();
        (await service.ExecuteAsync(Command(lotId, "Receive", 0, suffix) with
        {
            MaterialId = $"MAT_{suffix}", Quantity = 10m, Unit = "kg", Location = "STORE",
        })).IsSuccess.Should().BeTrue();

        var first = service.ExecuteAsync(Command(lotId, "Adjustment", 1, suffix) with
        {
            TransactionId = $"A_{suffix}", IdempotencyKey = $"A:{suffix}", SourceEventId = $"A:{suffix}",
            Quantity = -7m, Reason = "count A",
        });
        var second = service.ExecuteAsync(Command(lotId, "Adjustment", 1, suffix) with
        {
            TransactionId = $"B_{suffix}", IdempotencyKey = $"B:{suffix}", SourceEventId = $"B:{suffix}",
            Quantity = -6m, Reason = "count B",
        });
        var results = await Task.WhenAll(first, second);

        results.Count(x => x.IsSuccess).Should().Be(1);
        Scalar<decimal>("SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().BeOneOf(3m, 4m);
        Scalar<long>("SELECT VERSION_NO FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE LOT_ID=@lot", ("@lot", lotId))
            .Should().Be(2);
    }

    [Fact]
    public async Task Tx_insert_failure_rolls_back_lot_balance_and_version()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"ROLL_{suffix}";
        var service = Service();
        (await service.ExecuteAsync(Command(lotId, "Receive", 0, suffix) with
        {
            MaterialId = $"MAT_{suffix}", Quantity = 10m, Unit = "kg", Location = "STORE",
        })).IsSuccess.Should().BeTrue();
        var txId = $"FAIL_{suffix}";
        Exec($"""
            CREATE TRIGGER TRG_{suffix} BEFORE INSERT ON IVT_MATERIAL_TX
            WHEN NEW.TX_ID = '{txId}'
            BEGIN SELECT RAISE(ABORT, 'forced tx failure'); END;
            """);
        try
        {
            var act = () => service.ExecuteAsync(Command(lotId, "Adjustment", 1, suffix) with
            {
                TransactionId = txId, IdempotencyKey = $"FAIL:{suffix}",
                SourceEventId = $"FAIL:{suffix}", Quantity = -2m, Reason = "forced rollback",
            });

            await act.Should().ThrowAsync<SqliteException>();
            Scalar<decimal>("SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
                .Should().Be(10m);
            Scalar<long>("SELECT VERSION_NO FROM IVT_MATERIAL_LOT WHERE LOT_ID=@lot", ("@lot", lotId))
                .Should().Be(1);
            Scalar<long>("SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE TX_ID=@tx", ("@tx", txId))
                .Should().Be(0);
        }
        finally
        {
            Exec($"DROP TRIGGER IF EXISTS TRG_{suffix}");
        }
    }

    private MaterialLotService Service() => new(new MaterialLotRepository(DataSource()));

    private EesDataSource DataSource()
    {
        _ = _factory.CreateClient();
        return new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
    }

    private static MaterialLotCommand Command(string lotId, string operation, int version, string suffix) => new(
        $"RECV_{suffix}", $"RECV:{suffix}", operation, lotId, version, DateTime.UtcNow,
        "MES", $"RECV:{suffix}", ActorId: "operator-01", CorrelationId: $"CORR:{suffix}");

    private static MaterialConsumptionCommand Consumption(
        string lotId, string suffix, string eventName, decimal quantity) => new(
        $"C_{eventName}_{suffix}", $"C:{eventName}:{suffix}", "PLANT01", "EQ01", lotId,
        $"MAT_{suffix}", quantity, "kg", "Manual", DateTime.UtcNow, "MES",
        $"C:{eventName}:{suffix}", OperatorId: "operator-01", CorrelationId: $"CORR:{suffix}");

    private void Exec(string sql)
    {
        _ = _factory.CreateClient();
        using var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
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
}
