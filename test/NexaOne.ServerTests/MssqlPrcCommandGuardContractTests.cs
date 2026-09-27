using FluentAssertions;
using NexaOne.Application.Query;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Prc;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlPrcCommandGuardContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Module_purchase_commands_preserve_ordered_and_held_rows()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var registry = FileQueryRegistry.Load("mssql",
            RepositorySource.GetDirectory("src/00.Main/NexaOne.Server/config/db/queries"));
        registry.TryGet("PRC.OrderPurchaseOrder", out _).Should().BeFalse();
        registry.TryGet("PRC.ClosePurchaseOrder", out _).Should().BeFalse();
        registry.TryGet("PRC.CreatePurchaseOrder", out _).Should().BeFalse();
        registry.TryGet("PRC.DeletePurchaseOrder", out _).Should().BeFalse();
        var bridge = new NexaOne.PRC.Module(database.DataSource,
            new BusinessMasterDirectory(database.DataSource)).GetPurchaseOrderCommandBridge();
        var id = $"PRCG_{Guid.NewGuid():N}";
        Task<NexaOne.Common.Result<PurchaseOrderCommandState>> Save(string orderId, string name) =>
            bridge.SaveDraftAsync(new PurchaseOrderDraftCommand(
                orderId, "PLANT01", name, "V1", 10m, "prc-contract"));

        Task<string> Status() => database.ScalarAsync<string>(
            "SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id });
        Task<string> Name() => database.ScalarAsync<string>(
            "SELECT PURCHASE_ORDER_NAME FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id });

        (await Save(id, "original")).IsSuccess.Should().BeTrue();
        (await Save(id, "edited draft")).IsSuccess.Should().BeTrue();
        (await Name()).Should().Be("edited draft");

        (await bridge.OrderAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_TRANSITION_CONFLICT");
        (await Status()).Should().Be("Draft", "orders without any line cannot be placed");
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET STATUS='Incoming' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.CloseAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_TRANSITION_CONFLICT");
        (await Status()).Should().Be("Incoming", "an empty incoming order cannot be closed");
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET STATUS='Draft' WHERE PURCHASE_ORDER_ID=@id", new { id });
        await database.ExecuteAsync("""
            INSERT INTO PRC_PURCHASE_ITEM (PURCHASE_ORDER_ID, PRODUCT_ID, ORDER_QTY)
            VALUES (@id, 'TEST-PRODUCT', 10)
            """, new { id });

        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.OrderAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_TRANSITION_CONFLICT");
        (await Status()).Should().Be("Draft", "held Draft orders cannot be ordered");
        (await Save(id, "illegal edit")).Error.Code.Should().Be("PRC_ORDER_NOT_EDITABLE");
        (await Name()).Should().Be("edited draft", "held Draft orders cannot be edited");

        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='N' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.OrderAsync(id, "prc-contract")).Value.Status.Should().Be("Ordered");
        (await Status()).Should().Be("Ordered");
        (await Save(id, "illegal edit")).Error.Code.Should().Be("PRC_ORDER_NOT_EDITABLE");
        (await bridge.DeleteDraftAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_NOT_DELETABLE");
        (await Name()).Should().Be("edited draft", "Ordered orders cannot be edited or deleted");

        (await bridge.CloseAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_TRANSITION_CONFLICT");
        (await Status()).Should().Be("Ordered", "an order is not a completed receipt");
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET STATUS='Incoming' WHERE PURCHASE_ORDER_ID=@id", new { id });
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=5 WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.CloseAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_TRANSITION_CONFLICT");
        (await Status()).Should().Be("Incoming", "partial receipts cannot be closed");
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=10 WHERE PURCHASE_ORDER_ID=@id", new { id });
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.CloseAsync(id, "prc-contract")).Error.Code.Should().Be("PRC_ORDER_TRANSITION_CONFLICT");
        (await Status()).Should().Be("Incoming", "held received orders cannot be closed");
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='N' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.CloseAsync(id, "prc-contract")).Value.Status.Should().Be("Closed");
        (await Status()).Should().Be("Closed");

        var deletableId = $"PRCG_{Guid.NewGuid():N}";
        (await Save(deletableId, "draft to delete")).IsSuccess.Should().BeTrue();
        await database.ExecuteAsync("""
            INSERT INTO PRC_PURCHASE_ITEM (PURCHASE_ORDER_ID, PRODUCT_ID, ORDER_QTY)
            VALUES (@id, 'TEST-PRODUCT', 2)
            """, new { id = deletableId });
        (await bridge.DeleteDraftAsync(deletableId, "prc-contract")).IsSuccess.Should().BeTrue();
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id",
            new { id = deletableId })).Should().Be(0, "unheld Draft orders remain deletable");
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id",
            new { id = deletableId })).Should().Be(0, "deleted orders cannot leave orphan lines");
    }
}
