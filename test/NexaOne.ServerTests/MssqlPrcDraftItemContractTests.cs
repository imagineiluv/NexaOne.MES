using FluentAssertions;
using Microsoft.Data.SqlClient;
using NexaOne.Application.Query;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Prc;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlPrcDraftItemContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Draft_line_writes_keep_header_total_and_reject_held_or_received_items()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var id = $"PRCI_{suffix}";
        var product = $"MAT_{suffix}";
        await database.ExecuteAsync("""
            INSERT INTO MDM_PRODUCT (PRODUCT_ID,PRODUCT_NAME,PRODUCT_TYPE,UNIT,VALID_STATE)
            VALUES (@product,@product,'Material','EA','Valid');
            INSERT INTO PRC_PURCHASE_ORDER
                (PURCHASE_ORDER_ID,PLANT_ID,ORDER_QTY,STATUS,IS_HOLD)
            VALUES (@id,'PLANT01',0,'Draft','N');
            """, new { id, product });

        var module = new NexaOne.PRC.Module(database.DataSource,
            new BusinessMasterDirectory(database.DataSource));
        var bridge = module.GetPurchaseOrderItemBridge();

        var save = await bridge.SaveDraftItemAsync(id, product, 7m, "buyer");
        save.IsSuccess.Should().BeTrue();
        save.Value.OrderQuantity.Should().Be(7m);
        (await database.ScalarAsync<decimal>(
            "SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id",
            new { id })).Should().Be(7m);
        (await bridge.ListAsync(id)).Value.Should().ContainSingle()
            .Which.OrderQuantity.Should().Be(7m);

        var registry = FileQueryRegistry.Load("mssql",
            RepositorySource.GetDirectory("src/00.Main/NexaOne.Server/config/db/queries"));
        registry.TryGet("PRC.CreatePurchaseOrder", out _).Should().BeFalse();
        (await module.GetPurchaseOrderCommandBridge().SaveDraftAsync(new PurchaseOrderDraftCommand(
            id, "PLANT01", "edited", "V1", 999m, "buyer"))).IsSuccess.Should().BeTrue();
        (await database.ScalarAsync<decimal>(
            "SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id",
            new { id })).Should().Be(7m, "header-form command must preserve the item sum");

        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.SaveDraftItemAsync(id, product, 9m, "buyer")).IsFailure.Should().BeTrue();
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='N' WHERE PURCHASE_ORDER_ID=@id", new { id });
        await database.ExecuteAsync(
            "UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=2 WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await bridge.DeleteDraftItemAsync(id, product, "buyer")).IsFailure.Should().BeTrue();
        (await module.GetPurchaseOrderCommandBridge().DeleteDraftAsync(id, "buyer"))
            .Error.Code.Should().Be("PRC_ORDER_NOT_DELETABLE");
        (await database.ScalarAsync<decimal>(
            "SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id",
            new { id })).Should().Be(7m);
    }

    [Fact]
    public async Task Header_total_failure_rolls_back_new_line_and_same_command_can_retry()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var id = $"PRCI_{suffix}";
        var product = $"MAT_{suffix}";
        await database.ExecuteAsync("""
            INSERT INTO MDM_PRODUCT (PRODUCT_ID,PRODUCT_NAME,PRODUCT_TYPE,UNIT,VALID_STATE)
            VALUES (@product,@product,'Material','EA','Valid');
            INSERT INTO PRC_PURCHASE_ORDER
                (PURCHASE_ORDER_ID,PLANT_ID,ORDER_QTY,STATUS,IS_HOLD)
            VALUES (@id,'PLANT01',0,'Draft','N');
            """, new { id, product });
        var bridge = new NexaOne.PRC.Module(database.DataSource,
            new BusinessMasterDirectory(database.DataSource)).GetPurchaseOrderItemBridge();
        var trigger = $"prc_header_failure_{suffix}";
        await database.ExecuteAsync($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.PRC_PURCHASE_ORDER AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN deleted d ON d.PURCHASE_ORDER_ID=i.PURCHASE_ORDER_ID
                    WHERE i.PURCHASE_ORDER_ID='{id}' AND d.ORDER_QTY=0 AND i.ORDER_QTY=4
                )
                    THROW 51098, 'Injected PRC header total failure', 1;
            END;
            """);
        try
        {
            (await Assert.ThrowsAsync<SqlException>(() =>
                bridge.SaveDraftItemAsync(id, product, 4m, "buyer")))
                .Number.Should().Be(51098);
            (await database.ScalarAsync<int>(
                "SELECT COUNT(1) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id",
                new { id })).Should().Be(0);
            (await database.ScalarAsync<decimal>(
                "SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id",
                new { id })).Should().Be(0m);
        }
        finally
        {
            await database.ExecuteAsync($"DROP TRIGGER dbo.[{trigger}]");
        }

        var recovered = await bridge.SaveDraftItemAsync(id, product, 4m, "buyer");
        recovered.IsSuccess.Should().BeTrue(recovered.Error.Description);
        recovered.Value.OrderQuantity.Should().Be(4m);
        (await database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id",
            new { id })).Should().Be(1);
        (await database.ScalarAsync<decimal>(
            "SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id",
            new { id })).Should().Be(4m);
    }
}
