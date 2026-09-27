using FluentAssertions;
using NexaOne.MDM.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlPrcPurchaseOrderHoldContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Module_hold_commands_guard_states_and_preserve_status()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var id = $"PRCH_{Guid.NewGuid():N}";
        await database.ExecuteAsync("""
            INSERT INTO PRC_PURCHASE_ORDER
                (PURCHASE_ORDER_ID, PLANT_ID, ORDER_QTY, STATUS, IS_HOLD)
            VALUES (@id, 'PLANT01', 0, 'Draft', 'N')
            """, new { id });
        var bridge = new NexaOne.PRC.Module(database.DataSource,
            new BusinessMasterDirectory(database.DataSource)).GetPurchaseOrderHoldBridge();

        (await bridge.HoldAsync(id, "buyer")).Value.IsHeld.Should().BeTrue();
        (await bridge.HoldAsync(id, "buyer")).Error.Code.Should().Be("PRC_HOLD_TRANSITION_CONFLICT");
        (await database.ScalarAsync<string>(
            "SELECT UPDATED_BY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id }))
            .Should().Be("buyer");
        (await bridge.ReleaseAsync(id, "buyer")).Value.IsHeld.Should().BeFalse();

        await database.ExecuteAsync("UPDATE PRC_PURCHASE_ORDER SET STATUS='Incoming' WHERE PURCHASE_ORDER_ID=@id",
            new { id });
        (await bridge.HoldAsync(id, "buyer")).Error.Code.Should().Be("PRC_HOLD_TRANSITION_CONFLICT");
        await database.ExecuteAsync("UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id",
            new { id });
        (await bridge.ReleaseAsync(id, "buyer")).Value.IsHeld.Should().BeFalse();
        (await database.ScalarAsync<string>(
            "SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id }))
            .Should().Be("Incoming");
    }
}
