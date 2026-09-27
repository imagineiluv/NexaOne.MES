using FluentAssertions;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Ivt;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlMaterialLotSplitContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Split_is_atomic_replayable_and_rolls_back_both_ledger_legs_on_failure()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var parentId = $"P_{suffix}";
        var childId = $"C_{suffix}";
        var occurredAt = new DateTime(2026, 9, 28, 1, 2, 3, DateTimeKind.Utc);
        var receive = new MaterialLotCommand($"R_{suffix}", $"R:{suffix}", "Receive",
            parentId, 0, occurredAt, "MSSQL-SPLIT", $"R:{suffix}",
            MaterialId: $"MAT_{suffix}", Quantity: 10m, Unit: "kg", Location: "STORE",
            ActorId: "operator");
        (await new MaterialLotService(new MaterialLotRepository(database.DataSource))
            .ExecuteAsync(receive)).IsSuccess.Should().BeTrue();
        var command = new MaterialLotSplitCommand($"S_{suffix}", $"S:{suffix}",
            "MSSQL-SPLIT", $"S:{suffix}", parentId, childId, 1, 4m, occurredAt,
            ActorId: "operator");
        MaterialLotSplitService Service() => new(new MaterialLotSplitRepository(database.DataSource));

        var applied = await Service().SplitAsync(command);
        var replay = await Service().SplitAsync(command);
        var altered = await Service().SplitAsync(command with { Quantity = 5m });
        var origin = await Service().GetOriginAsync(childId);

        applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.Description : string.Empty);
        replay.Value.IsReplay.Should().BeTrue();
        altered.Error.Code.Should().Be("IVT_SPLIT_CONFLICT");
        origin.IsSuccess.Should().BeTrue(origin.IsFailure ? origin.Error.Description : string.Empty);
        origin.Value.SplitId.Should().Be(command.SplitId);
        origin.Value.ParentLotId.Should().Be(parentId);
        origin.Value.ChildLotId.Should().Be(childId);
        origin.Value.Quantity.Should().Be(4m);
        origin.Value.ParentTransactionId.Should().NotBeNullOrWhiteSpace();
        origin.Value.ChildTransactionId.Should().NotBeNullOrWhiteSpace();
        (await database.ScalarAsync<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@parentId", new { parentId }))
            .Should().Be(6m);
        (await database.ScalarAsync<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@childId", new { childId }))
            .Should().Be(4m);
        (await database.ScalarAsync<int>("""
            SELECT COUNT(*) FROM IVT_MATERIAL_TX
             WHERE CORRELATION_ID=@splitId AND TX_TYPE IN ('SplitOut','SplitIn')
            """, new { splitId = command.SplitId })).Should().Be(2);

        var sibling = command with
        {
            SplitId = $"S2_{suffix}", IdempotencyKey = $"S2:{suffix}",
            SourceEventId = $"S2:{suffix}", ChildLotId = $"C2_{suffix}",
            ExpectedParentVersion = 2, Quantity = 1m,
        };
        (await Service().SplitAsync(sibling)).IsSuccess.Should().BeTrue();
        var firstPage = await Service().GetChildrenAsync(parentId, limit: 1);
        firstPage.IsSuccess.Should().BeTrue(firstPage.IsFailure ? firstPage.Error.Description : string.Empty);
        firstPage.Value.Items.Should().ContainSingle();
        firstPage.Value.NextAfterSplitId.Should().NotBeNull();
        var secondPage = await Service().GetChildrenAsync(parentId, firstPage.Value.NextAfterSplitId, 1);
        secondPage.IsSuccess.Should().BeTrue(secondPage.IsFailure ? secondPage.Error.Description : string.Empty);
        secondPage.Value.Items.Should().ContainSingle();
        secondPage.Value.NextAfterSplitId.Should().BeNull();
        new[] { firstPage.Value.Items[0].SplitId, secondPage.Value.Items[0].SplitId }
            .Should().BeEquivalentTo(new[] { command.SplitId, sibling.SplitId });
        (await Service().GetChildrenAsync(childId)).Value.Items.Should().BeEmpty();
        (await Service().GetChildrenAsync(parentId, "S_NOT_FOUND"))
            .Error.Code.Should().Be("IVT_SPLIT_CURSOR_INVALID");

        var grandchild = command with
        {
            SplitId = $"S3_{suffix}", IdempotencyKey = $"S3:{suffix}",
            SourceEventId = $"S3:{suffix}", ParentLotId = childId,
            ChildLotId = $"GC_{suffix}", ExpectedParentVersion = 1, Quantity = 1m,
        };
        (await Service().SplitAsync(grandchild)).IsSuccess.Should().BeTrue();
        await database.ExecuteAsync("""
            UPDATE IVT_MATERIAL_LOT_SPLIT SET CREATED_AT=@createdAt
             WHERE SPLIT_ID IN (@firstId, @secondId, @grandchildId)
            """, new
        {
            createdAt = occurredAt,
            firstId = command.SplitId,
            secondId = sibling.SplitId,
            grandchildId = grandchild.SplitId,
        });
        var descendant1 = await Service().GetDescendantsAsync(parentId, limit: 1);
        var descendant2 = await Service().GetDescendantsAsync(
            parentId, descendant1.Value.NextAfterSplitId, 1);
        var descendant3 = await Service().GetDescendantsAsync(
            parentId, descendant2.Value.NextAfterSplitId, 1);
        descendant1.IsSuccess.Should().BeTrue(descendant1.IsFailure ? descendant1.Error.Description : string.Empty);
        descendant2.IsSuccess.Should().BeTrue(descendant2.IsFailure ? descendant2.Error.Description : string.Empty);
        descendant3.IsSuccess.Should().BeTrue(descendant3.IsFailure ? descendant3.Error.Description : string.Empty);
        new[] { descendant1, descendant2, descendant3 }
            .Select(page => page.Value.Items.Single().Origin.SplitId)
            .Should().Equal(command.SplitId, sibling.SplitId, grandchild.SplitId);
        new[] { descendant1, descendant2, descendant3 }
            .Select(page => page.Value.Items.Single().Depth).Should().Equal(1, 1, 2);
        descendant3.Value.NextAfterSplitId.Should().BeNull();
        (await Service().GetDescendantsAsync(parentId, "S_NOT_FOUND"))
            .Error.Code.Should().Be("IVT_SPLIT_CURSOR_INVALID");

        var rollbackParent = $"RP_{suffix}";
        var rollbackChild = $"RC_{suffix}";
        (await new MaterialLotService(new MaterialLotRepository(database.DataSource))
            .ExecuteAsync(receive with
            {
                TransactionId = $"RR_{suffix}", IdempotencyKey = $"RR:{suffix}",
                MaterialLotId = rollbackParent, SourceEventId = $"RR:{suffix}",
            })).IsSuccess.Should().BeTrue();
        var rollbackCommand = command with
        {
            SplitId = $"RS_{suffix}", IdempotencyKey = $"RS:{suffix}",
            SourceEventId = $"RS:{suffix}", ParentLotId = rollbackParent,
            ChildLotId = rollbackChild,
        };
        var triggerName = $"TR_SPLIT_FAIL_{suffix}";
        await database.ExecuteAsync($"""
            CREATE TRIGGER {triggerName} ON IVT_MATERIAL_TX AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted
                            WHERE TX_TYPE='SplitIn' AND LOT_ID='{rollbackChild}')
                    THROW 51238, 'injected split failure', 1;
            END
            """);
        try
        {
            var failure = await FluentActions.Awaiting(() => Service().SplitAsync(rollbackCommand))
                .Should().ThrowAsync<Exception>();
            failure.Which.ToString().Should().Contain("injected split failure");
        }
        finally
        {
            await database.ExecuteAsync($"DROP TRIGGER {triggerName}");
        }

        (await database.ScalarAsync<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@rollbackParent",
            new { rollbackParent })).Should().Be(10m);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT WHERE LOT_ID=@rollbackChild",
            new { rollbackChild })).Should().Be(0);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT_SPLIT WHERE SPLIT_ID=@id",
            new { id = rollbackCommand.SplitId })).Should().Be(0);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_TX WHERE CORRELATION_ID=@id",
            new { id = rollbackCommand.SplitId })).Should().Be(0);
        (await Service().SplitAsync(rollbackCommand)).IsSuccess.Should().BeTrue();

        var raceParent = $"CP_{suffix}";
        (await new MaterialLotService(new MaterialLotRepository(database.DataSource))
            .ExecuteAsync(receive with
            {
                TransactionId = $"CR_{suffix}", IdempotencyKey = $"CR:{suffix}",
                MaterialLotId = raceParent, SourceEventId = $"CR:{suffix}",
            })).IsSuccess.Should().BeTrue();
        var firstRace = command with
        {
            SplitId = $"CS1_{suffix}", IdempotencyKey = $"CS1:{suffix}",
            SourceEventId = $"CS1:{suffix}", ParentLotId = raceParent,
            ChildLotId = $"CC1_{suffix}",
        };
        var secondRace = firstRace with
        {
            SplitId = $"CS2_{suffix}", IdempotencyKey = $"CS2:{suffix}",
            SourceEventId = $"CS2:{suffix}", ChildLotId = $"CC2_{suffix}",
        };
        var raced = await Task.WhenAll(
            Service().SplitAsync(firstRace), Service().SplitAsync(secondRace));
        raced.Count(result => result.IsSuccess).Should().Be(1);
        raced.Count(result => result.IsFailure).Should().Be(1);
        (await database.ScalarAsync<decimal>(
            "SELECT CURRENT_QTY FROM IVT_MATERIAL_LOT WHERE LOT_ID=@raceParent",
            new { raceParent })).Should().Be(6m);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM IVT_MATERIAL_LOT_SPLIT WHERE PARENT_LOT_ID=@raceParent",
            new { raceParent })).Should().Be(1);
    }
}
