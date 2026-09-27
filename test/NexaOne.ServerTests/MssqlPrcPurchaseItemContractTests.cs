using FluentAssertions;
using NexaOne.ServiceContracts.Prc;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlPrcPurchaseItemContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Mrp_purchase_header_and_line_commit_together_and_receipts_use_remaining_line_quantity()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var id = $"PRCL_{Guid.NewGuid():N}";
        var product = $"MAT_{Guid.NewGuid():N}"[..20];
        var bridge = new NexaOne.PRC.Module(database.DataSource).GetPurchaseOrderPlanningBridge();
        var request = new MrpPurchaseOrderRequest(
            id, "PLANT01", "contract purchase", DateTime.UtcNow,
            DateTime.UtcNow.AddDays(7), 12m, product, "MRP contract", "prc-contract");

        (await bridge.EnsureMrpPurchaseOrderAsync(request)).Created.Should().BeTrue();
        (await bridge.EnsureMrpPurchaseOrderAsync(request)).Created.Should().BeFalse();
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id " +
            "AND PRODUCT_ID=@product AND ORDER_QTY=12", new { id, product }))
            .Should().Be(1);

        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=4 WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.GetScheduledReceiptsAsync()).Single(row => row.ProductId == product)
            .Quantity.Should().Be(8m);
    }
}
