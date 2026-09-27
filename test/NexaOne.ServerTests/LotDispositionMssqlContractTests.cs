using FluentAssertions;
using Microsoft.Data.SqlClient;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class LotDispositionMssqlContractTests(ITestOutputHelper output)
{
    [StockMssqlFact]
    public async Task Response_loss_after_commit_replays_one_disposition_without_touching_the_lot_again()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"DL_{suffix}";
        var plantId = $"DP_{suffix}";
        var key = $"DISP:LOST:{suffix}";
        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6);
        await database.ExecuteAsync("""
            INSERT INTO POM_LOT
                (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY,
                 LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD,
                 VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_AT)
            VALUES
                (@lotId, @plantId, NULL, @productId, 10, 3,
                 'Completed', 'Idle', 'CUT', 0, 'N', 1,
                 'mssql-contract', SYSUTCDATETIME(), @sentinel);
            """, new { lotId, plantId, productId = $"ITEM_{suffix}", sentinel });
        var command = new LotDispositionCommand(
            plantId, lotId, null, null, null, null, "Scrap", 1m, "QUALITY",
            "confirmed defect", "operator01", key, "MES", null, null);
        var provider = new AfterCommitResponseLossProvider(database.DataSource.Provider);
        var faultingSource = new EesDataSource
        {
            Provider = provider,
            ConnectionString = database.ConnectionString,
            QueryGatewayOptions = database.DataSource.QueryGatewayOptions,
        };
        var faultingService = new LotDispositionService(new LotDispositionRepository(faultingSource));

        (await Assert.ThrowsAsync<IOException>(() => faultingService.RecordAsync(command)))
            .Message.Should().Contain("response loss");
        provider.CallbackCount.Should().Be(1);
        var dispositionId = await database.ScalarAsync<string>(
            "SELECT DISPOSITION_ID FROM POM_LOT_DISPOSITION WHERE IDEMPOTENCY_KEY=@key",
            new { key });
        var updatedAt = await database.ScalarAsync<DateTime>(
            "SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lotId", new { lotId });
        updatedAt.Should().BeAfter(sentinel);

        var recovered = await new LotDispositionService(new LotDispositionRepository(database.DataSource))
            .RecordAsync(command);
        recovered.IsSuccess.Should().BeTrue(recovered.IsFailure ? recovered.Error.Description : string.Empty);
        recovered.Value.DispositionId.Should().Be(dispositionId);
        var conflicting = await new LotDispositionService(new LotDispositionRepository(database.DataSource))
            .RecordAsync(command with { Quantity = 2m });
        conflicting.IsFailure.Should().BeTrue();
        conflicting.Error.Code.Should().Be("POM.LotDisposition.IdempotencyConflict");
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE IDEMPOTENCY_KEY=@key",
            new { key })).Should().Be(1);
        (await database.ScalarAsync<DateTime>(
            "SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lotId", new { lotId }))
            .Should().Be(updatedAt);
    }

    [StockMssqlFact]
    public async Task Insert_failure_rolls_back_lot_touch_then_same_key_retries_once()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var lotId = $"DL_{suffix}";
        var plantId = $"DP_{suffix}";
        var key = $"DISP:ROLLBACK:{suffix}";
        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6);
        await database.ExecuteAsync("""
            INSERT INTO POM_LOT
                (LOT_ID, PLANT_ID, WORK_ORDER_ID, PRODUCT_ID, QTY, DEFECT_QTY,
                 LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD,
                 VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_AT)
            VALUES
                (@lotId, @plantId, NULL, @productId, 10, 3,
                 'Completed', 'Idle', 'CUT', 0, 'N', 1,
                 'mssql-contract', SYSUTCDATETIME(), @sentinel);
            """, new { lotId, plantId, productId = $"ITEM_{suffix}", sentinel });

        var command = new LotDispositionCommand(
            plantId, lotId, null, null, null, null, "Scrap", 1m, "QUALITY",
            "confirmed defect", "operator01", key, "MES", null, null);
        var service = new LotDispositionService(new LotDispositionRepository(database.DataSource));
        var trigger = $"lot_disposition_test_{suffix}";
        await database.ExecuteAsync($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.POM_LOT_DISPOSITION AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE IDEMPOTENCY_KEY = '{key}')
                    THROW 51098, 'Injected disposition insert failure', 1;
            END;
            """);

        try
        {
            (await Assert.ThrowsAsync<SqlException>(() => service.RecordAsync(command)))
                .Number.Should().Be(51098);
            (await database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE IDEMPOTENCY_KEY=@key",
                new { key })).Should().Be(0);
            (await database.ScalarAsync<DateTime>(
                "SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lotId", new { lotId }))
                .Should().Be(sentinel);
        }
        finally
        {
            await database.ExecuteAsync($"DROP TRIGGER dbo.[{trigger}]");
        }

        var recovered = await new LotDispositionService(new LotDispositionRepository(database.DataSource))
            .RecordAsync(command);
        recovered.IsSuccess.Should().BeTrue(recovered.IsFailure ? recovered.Error.Description : string.Empty);
        var updatedAt = await database.ScalarAsync<DateTime>(
            "SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lotId", new { lotId });
        updatedAt.Should().BeAfter(sentinel);

        var replay = await new LotDispositionService(new LotDispositionRepository(database.DataSource))
            .RecordAsync(command);
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        replay.Value.DispositionId.Should().Be(recovered.Value.DispositionId);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM POM_LOT_DISPOSITION WHERE IDEMPOTENCY_KEY=@key",
            new { key })).Should().Be(1);
        (await database.ScalarAsync<DateTime>(
            "SELECT UPDATED_AT FROM POM_LOT WHERE LOT_ID=@lotId", new { lotId }))
            .Should().Be(updatedAt);
    }
}
