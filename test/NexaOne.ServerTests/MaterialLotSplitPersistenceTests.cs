using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MaterialLotSplitPersistenceTests :
    IClassFixture<IvtTraceProjectionPersistenceTests.TraceFactory>
{
    private static readonly DateTime OccurredAt = new(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc);
    private readonly IvtTraceProjectionPersistenceTests.TraceFactory _factory;

    public MaterialLotSplitPersistenceTests(IvtTraceProjectionPersistenceTests.TraceFactory factory)
        => _factory = factory;

    [Fact]
    public async Task Split_conserves_stock_and_replays_without_another_ledger_write()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        await Receive(parentId, suffix, 10m);
        var command = Split(parentId, childId, suffix, 4.125m);

        var applied = await Service().SplitAsync(command);
        var replay = await Service().SplitAsync(command);
        var changed = await Service().SplitAsync(command with { Quantity = 5m });
        var sourceCollision = await Service().SplitAsync(command with
        {
            SplitId = $"OTHER_{suffix}", IdempotencyKey = $"OTHER:{suffix}",
            ChildLotId = $"OTHER_CHILD_{suffix}",
        });
        var caseVariantKey = await Service().SplitAsync(command with
        {
            IdempotencyKey = command.IdempotencyKey.ToLowerInvariant(),
        });

        applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.Description : string.Empty);
        applied.Value.Should().Be(new MaterialLotSplitDto(command.SplitId, parentId, childId,
            4.125m, 10m, 5.875m, 2, "InStock", "InStock", false));
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(applied.Value with { IsReplay = true });
        changed.Error.Type.Should().Be(ErrorType.Conflict);
        sourceCollision.Error.Type.Should().Be(ErrorType.Conflict);
        caseVariantKey.Error.Type.Should().Be(ErrorType.Conflict);
        using var connection = Connection();
        connection.QuerySingle<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId", new { parentId })
            .Should().Be(5.875m);
        connection.QuerySingle<(string MaterialId, string Unit, string Warehouse, decimal Balance,
            string LotNumber, string Status, long Version)>("""
            SELECT MATERIAL_ID AS MaterialId, UNIT AS Unit, WAREHOUSE AS Warehouse,
                   CURRENT_QTY AS Balance, LOT_NO AS LotNumber, STATUS AS Status,
                   VERSION_NO AS Version
              FROM IVT_MATERIAL_LOT WHERE LOT_ID=@childId
            """, new { childId }).Should().Be(($"MAT_{suffix}", "kg", "STORE",
                4.125m, $"LOT_{suffix}", "InStock", 1L));
        connection.QuerySingle<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT_SPLIT WHERE SPLIT_ID=@id",
            new { id = command.SplitId }).Should().Be(1);
        connection.QuerySingle<long>("""
            SELECT COUNT(*) FROM IVT_MATERIAL_TX
             WHERE CORRELATION_ID=@id AND TX_TYPE IN ('SplitOut','SplitIn')
            """, new { id = command.SplitId }).Should().Be(2);
        connection.QuerySingle<decimal>("""
            SELECT SUM(BALANCE_DELTA) FROM IVT_MATERIAL_TX
             WHERE CORRELATION_ID=@id AND TX_TYPE IN ('SplitOut','SplitIn')
            """, new { id = command.SplitId }).Should().Be(0m);
    }

    [Fact]
    public async Task Split_rejects_invalid_parent_state_identity_and_quantity_without_writes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        await Receive(parentId, suffix, 3m);
        var command = Split(parentId, childId, suffix, 1m);

        (await Service().SplitAsync(command with { ChildLotId = parentId }))
            .Error.Type.Should().Be(ErrorType.Validation);
        (await Service().SplitAsync(command with { Quantity = 0.0000001m }))
            .Error.Type.Should().Be(ErrorType.Validation);
        (await Service().SplitAsync(command with { Quantity = 4m }))
            .Error.Type.Should().Be(ErrorType.Conflict);
        (await Service().SplitAsync(command with { ExpectedParentVersion = 2 }))
            .Error.Type.Should().Be(ErrorType.Conflict);
        (await Service().SplitAsync(command with { ParentLotId = "NOT_FOUND" }))
            .Error.Type.Should().Be(ErrorType.NotFound);
        var existingChildId = $"EXISTING_{suffix}";
        await Receive(existingChildId, $"EXISTING_{suffix}", 2m);
        (await Service().SplitAsync(command with { ChildLotId = existingChildId }))
            .Error.Type.Should().Be(ErrorType.Conflict);
        (await Service().SplitAsync(command with { IdempotencyKey = $"R:{suffix}" }))
            .Error.Type.Should().Be(ErrorType.Conflict,
                "a split may not reuse a Receive idempotency key");
        (await Service().SplitAsync(command with { SourceEventId = $"R:{suffix}" }))
            .Error.Type.Should().Be(ErrorType.Conflict,
                "a split may not reuse a Receive source event");

        var hold = new MaterialLotCommand($"H_{suffix}", $"H:{suffix}", "Hold", parentId, 1,
            OccurredAt, "TEST", $"H:{suffix}", Reason: "inspection", ActorId: "operator");
        (await LotService().ExecuteAsync(hold)).IsSuccess.Should().BeTrue();
        (await Service().SplitAsync(command with { ExpectedParentVersion = 2 }))
            .Error.Type.Should().Be(ErrorType.Conflict);
        using var connection = Connection();
        connection.QuerySingle<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId", new { parentId })
            .Should().Be(3m);
        connection.QuerySingle<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID=@childId", new { childId })
            .Should().Be(0);
    }

    [Fact]
    public async Task Whole_balance_split_consumes_parent_and_keeps_child_in_the_lot_lifecycle()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        await Receive(parentId, suffix, 3m);

        (await Service().SplitAsync(Split(parentId, childId, suffix, 3m)))
            .Value.ParentStatus.Should().Be("Consumed");
        var childHold = new MaterialLotCommand($"H_{suffix}", $"H:{suffix}", "Hold",
            childId, 1, OccurredAt, "TEST", $"H:{suffix}",
            Reason: "quality review", ActorId: "operator");
        (await LotService().ExecuteAsync(childHold)).IsSuccess.Should().BeTrue(
            "the child belongs to the existing LOT lifecycle, not a separate stock store");

        using var connection = Connection();
        connection.QuerySingle<(decimal Balance, string Status)>("""
            SELECT CURRENT_QTY AS Balance, STATUS AS Status
              FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId
            """, new { parentId }).Should().Be((0m, "Consumed"));
        connection.QuerySingle<(decimal Balance, string Status)>("""
            SELECT CURRENT_QTY AS Balance, STATUS AS Status
              FROM IVT_MATERIAL_LOT WHERE LOT_ID=@childId
            """, new { childId }).Should().Be((3m, "Hold"));
    }

    [Fact]
    public async Task Origin_query_walks_direct_split_edges_and_returns_immutable_ledger_evidence()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        var grandchildId = $"G_{suffix}";
        await Receive(parentId, suffix, 10m);
        var first = Split(parentId, childId, suffix, 4m);
        var second = Split(childId, grandchildId, $"GRAND_{suffix}", 1.5m);
        (await Service().SplitAsync(first)).IsSuccess.Should().BeTrue();
        (await Service().SplitAsync(second)).IsSuccess.Should().BeTrue();

        (await Service().GetOriginAsync(" ")).Error.Type.Should().Be(ErrorType.Validation);
        (await Service().GetOriginAsync(parentId)).Error.Type.Should().Be(ErrorType.NotFound);
        var origin = await Service().GetOriginAsync($" {childId} ");
        var grandchildOrigin = await Service().GetOriginAsync(grandchildId);

        origin.IsSuccess.Should().BeTrue(origin.IsFailure ? origin.Error.Description : string.Empty);
        grandchildOrigin.IsSuccess.Should().BeTrue();
        origin.Value.Should().BeEquivalentTo(new
        {
            first.SplitId,
            ParentLotId = parentId,
            ChildLotId = childId,
            ChildLotNumber = first.ChildLotNumber,
            Quantity = 4m,
            ParentBalanceBefore = 10m,
            ParentBalanceAfter = 6m,
            ParentVersion = 2,
            ParentStatus = "InStock",
            OccurredAt,
            ActorId = "operator",
            SourceSystem = "TEST",
            SourceEventId = first.SourceEventId,
        });
        grandchildOrigin.Value.ParentLotId.Should().Be(childId);
        grandchildOrigin.Value.ChildLotId.Should().Be(grandchildId);
        grandchildOrigin.Value.Quantity.Should().Be(1.5m);
        using var connection = Connection();
        connection.QuerySingle<(string LotId, string Type)>(
            "SELECT LOT_ID AS LotId, TX_TYPE AS Type FROM IVT_MATERIAL_TX WHERE TX_ID=@txId",
            new { txId = origin.Value.ParentTransactionId })
            .Should().Be((parentId, "SplitOut"));
        connection.QuerySingle<(string LotId, string Type)>(
            "SELECT LOT_ID AS LotId, TX_TYPE AS Type FROM IVT_MATERIAL_TX WHERE TX_ID=@txId",
            new { txId = origin.Value.ChildTransactionId })
            .Should().Be((childId, "SplitIn"));
    }

    [Fact]
    public async Task Children_query_pages_direct_edges_once_even_when_creation_times_tie()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var firstChildId = $"C_A_{suffix}";
        await Receive(parentId, suffix, 10m);
        var first = Split(parentId, firstChildId, $"A_{suffix}", 2m);
        var second = Split(parentId, $"C_B_{suffix}", $"B_{suffix}", 2m)
            with { ExpectedParentVersion = 2 };
        var third = Split(parentId, $"C_C_{suffix}", $"C_{suffix}", 2m)
            with { ExpectedParentVersion = 3 };
        var grandchild = Split(firstChildId, $"G_{suffix}", $"G_{suffix}", 1m);
        foreach (var command in new[] { first, second, third, grandchild })
        {
            var result = await Service().SplitAsync(command);
            result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        }

        using (var connection = Connection())
            connection.Execute("""
                UPDATE IVT_MATERIAL_LOT_SPLIT SET CREATED_AT=@createdAt
                 WHERE PARENT_LOT_ID=@parentId
                """, new { parentId, createdAt = OccurredAt });

        var page1 = await Service().GetChildrenAsync($" {parentId} ", limit: 1);
        var page2 = await Service().GetChildrenAsync(parentId, $" {page1.Value.NextAfterSplitId} ", 1);
        var page3 = await Service().GetChildrenAsync(parentId, page2.Value.NextAfterSplitId, 1);
        var exhausted = await Service().GetChildrenAsync(parentId, third.SplitId, 1);
        var all = await Service().GetChildrenAsync(parentId);
        var grandchildren = await Service().GetChildrenAsync(firstChildId);
        var childless = await Service().GetChildrenAsync(second.ChildLotId);

        page1.IsSuccess.Should().BeTrue(page1.IsFailure ? page1.Error.Description : string.Empty);
        page2.IsSuccess.Should().BeTrue(page2.IsFailure ? page2.Error.Description : string.Empty);
        page3.IsSuccess.Should().BeTrue(page3.IsFailure ? page3.Error.Description : string.Empty);
        new[] { page1.Value.Items.Single().SplitId, page2.Value.Items.Single().SplitId,
            page3.Value.Items.Single().SplitId }
            .Should().Equal(first.SplitId, second.SplitId, third.SplitId);
        page1.Value.NextAfterSplitId.Should().Be(first.SplitId);
        page2.Value.NextAfterSplitId.Should().Be(second.SplitId);
        page3.Value.NextAfterSplitId.Should().BeNull();
        exhausted.Value.Items.Should().BeEmpty();
        exhausted.Value.NextAfterSplitId.Should().BeNull();
        all.Value.Items.Select(item => item.SplitId).Should()
            .Equal(first.SplitId, second.SplitId, third.SplitId);
        all.Value.Items.Select(item => item.ParentVersion).Should().Equal(2, 3, 4);
        all.Value.Items.Select(item => item.ParentBalanceAfter).Should().Equal(8m, 6m, 4m);
        all.Value.Items.Should().OnlyContain(item =>
            item.ParentLotId == parentId &&
            !string.IsNullOrWhiteSpace(item.ParentTransactionId) &&
            !string.IsNullOrWhiteSpace(item.ChildTransactionId));
        grandchildren.Value.Items.Select(item => item.SplitId).Should().Equal(grandchild.SplitId);
        childless.Value.Items.Should().BeEmpty();
        childless.Value.NextAfterSplitId.Should().BeNull();
    }

    [Fact]
    public async Task Children_query_rejects_invalid_scope_cursor_and_page_size()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var otherParentId = $"OP_{suffix}";
        await Receive(parentId, suffix, 3m);
        await Receive(otherParentId, $"O_{suffix}", 3m);
        var otherSplit = Split(otherParentId, $"OC_{suffix}", $"O_{suffix}", 1m);
        (await Service().SplitAsync(otherSplit)).IsSuccess.Should().BeTrue();

        (await Service().GetChildrenAsync(" ")).Error.Type.Should().Be(ErrorType.Validation);
        (await Service().GetChildrenAsync(parentId, " ")).Error.Type.Should().Be(ErrorType.Validation);
        (await Service().GetChildrenAsync(parentId, limit: 0)).Error.Type.Should().Be(ErrorType.Validation);
        (await Service().GetChildrenAsync(parentId, limit: 101)).Error.Type.Should().Be(ErrorType.Validation);
        (await Service().GetChildrenAsync("NOT_FOUND")).Error.Type.Should().Be(ErrorType.NotFound);
        (await Service().GetChildrenAsync(parentId, otherSplit.SplitId))
            .Error.Code.Should().Be("IVT_SPLIT_CURSOR_INVALID");
        (await Service().GetChildrenAsync(parentId, "S_NOT_FOUND"))
            .Error.Type.Should().Be(ErrorType.Validation);
        (await Service().GetChildrenAsync(parentId, limit: 100)).Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Mounted_or_unmounted_but_reserved_parent_cannot_be_split()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        await Receive(parentId, suffix, 3m);
        var command = Split(parentId, childId, suffix, 1m);
        var mountedAt = DateTime.UtcNow.AddMinutes(-2);
        var feed = new FeedSessionService(
            new FeedSessionRepository(DataSource()), new MaterialLotRepository(DataSource()));
        var mount = new FeedSessionCommand(
            FeedSessionOperations.Mount, $"FS_{suffix}", 0, $"FS:{suffix}",
            "TEST", $"FS:{suffix}", mountedAt,
            PlantId: $"PLANT_{suffix}", EquipmentId: $"EQ_{suffix}",
            FeedPointId: "FEED-01", MaterialLotId: parentId,
            MaterialId: $"MAT_{suffix}", ActorId: "operator");
        (await feed.ExecuteAsync(mount)).IsSuccess.Should().BeTrue();
        (await Service().SplitAsync(command)).Error.Type.Should().Be(ErrorType.Conflict);

        var unmount = new FeedSessionCommand(
            FeedSessionOperations.Unmount, mount.FeedSessionId, 1, $"FU:{suffix}",
            "TEST", $"FU:{suffix}", mountedAt.AddMinutes(1),
            ActorId: "operator", Reason: "test changeover");
        (await feed.ExecuteAsync(unmount)).IsSuccess.Should().BeTrue();
        (await Service().SplitAsync(command)).Error.Type.Should().Be(ErrorType.Conflict,
            "the LOT reservation survives physical unmount until trace finalization");

        using var connection = Connection();
        connection.QuerySingle<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId", new { parentId })
            .Should().Be(3m);
        connection.QuerySingle<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID=@childId", new { childId })
            .Should().Be(0);
    }

    [Fact]
    public async Task Child_ledger_failure_rolls_back_parent_child_and_genealogy()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        await Receive(parentId, suffix, 8m);
        var command = Split(parentId, childId, suffix, 2m);
        var triggerName = $"TR_SPLIT_FAIL_{suffix}";
        using (var connection = Connection())
            connection.Execute($"""
                CREATE TRIGGER {triggerName} AFTER INSERT ON IVT_MATERIAL_TX
                WHEN NEW.TX_TYPE='SplitIn' AND NEW.LOT_ID='{childId}'
                BEGIN SELECT RAISE(ABORT, 'injected split failure'); END
                """);

        try
        {
            await FluentActions.Awaiting(() => Service().SplitAsync(command))
                .Should().ThrowAsync<DbException>();
        }
        finally
        {
            using var connection = Connection();
            connection.Execute($"DROP TRIGGER {triggerName}");
        }

        using var verify = Connection();
        verify.QuerySingle<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId", new { parentId })
            .Should().Be(8m);
        verify.QuerySingle<long>(
            "SELECT VERSION_NO FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId", new { parentId })
            .Should().Be(1);
        verify.QuerySingle<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID=@childId", new { childId })
            .Should().Be(0);
        verify.QuerySingle<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT_SPLIT WHERE SPLIT_ID=@id",
            new { id = command.SplitId }).Should().Be(0);
        verify.QuerySingle<long>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE CORRELATION_ID=@id",
            new { id = command.SplitId }).Should().Be(0);

        (await Service().SplitAsync(command)).IsSuccess.Should().BeTrue(
            "a rolled-back split must be safely retryable with the same key");
    }

    private async Task Receive(string lotId, string suffix, decimal quantity)
    {
        var command = new MaterialLotCommand($"R_{suffix}", $"R:{suffix}", "Receive", lotId, 0,
            OccurredAt, "TEST", $"R:{suffix}", MaterialId: $"MAT_{suffix}",
            LotNumber: $"ORIGINAL_{suffix}", Quantity: quantity, Unit: "kg",
            Location: "STORE", ActorId: "operator");
        var received = await LotService().ExecuteAsync(command);
        received.IsSuccess.Should().BeTrue(received.IsFailure ? received.Error.Description : string.Empty);
    }

    private static MaterialLotSplitCommand Split(
        string parentId, string childId, string suffix, decimal quantity) =>
        new($"S_{suffix}", $"S:{suffix}", "TEST", $"S:{suffix}",
            parentId, childId, 1, quantity, OccurredAt,
            ChildLotNumber: $"LOT_{suffix}", ActorId: "operator");

    private MaterialLotService LotService() => new(new MaterialLotRepository(DataSource()));
    private MaterialLotSplitService Service() => new(new MaterialLotSplitRepository(DataSource()));

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnectionString,
    };

    private SqliteConnection Connection()
    {
        var connection = new SqliteConnection(_factory.ConnectionString);
        connection.Open();
        return connection;
    }
}
