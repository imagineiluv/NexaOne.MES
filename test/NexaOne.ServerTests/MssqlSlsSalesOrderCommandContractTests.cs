using FluentAssertions;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Sls;
using NexaOne.SHP.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlSlsSalesOrderCommandContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Module_writer_guards_draft_link_and_state_transitions_on_mssql()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var plant = $"SLSP_{suffix}";
        var customer = $"SLSC_{suffix}";
        var product = $"SLSI_{suffix}";
        var salesOrderId = $"SLSO_{suffix}";
        await database.ExecuteAsync("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@plant, @plant);
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
                VALUES (@customer, @customer, 1);
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES (@product, @product, 'FinishedGoods', 'EA', 'Valid');
            """, new { plant, customer, product });

        var bridge = new NexaOne.SLS.Module(
            database.DataSource, new BusinessMasterDirectory(database.DataSource),
            new SalesOrderShipmentIntake(), new SalesOrderShipmentEvidence())
            .GetSalesOrderCommandBridge();
        var draft = new SalesOrderDraftCommand(
            salesOrderId, "original", plant, customer, product,
            new DateTime(2040, 9, 1), new DateTime(2040, 9, 30), 12.5m, "seller");
        (await bridge.SaveDraftAsync(draft)).IsSuccess.Should().BeTrue();
        (await bridge.SaveDraftAsync(draft with { SalesOrderName = "edited", ActorId = "editor" }))
            .IsSuccess.Should().BeTrue();
        (await database.ScalarAsync<string>(
            "SELECT SALES_ORDER_NAME FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId })).Should().Be("edited");
        (await database.ScalarAsync<string>(
            "SELECT CREATED_BY FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId })).Should().Be("seller");

        await database.ExecuteAsync(
            "UPDATE SLS_SALES_ORDER SET IS_HOLD='Y' WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId });
        (await bridge.SaveDraftAsync(draft with { SalesOrderName = "illegal" }))
            .IsFailure.Should().BeTrue();
        (await bridge.DeleteDraftAsync(salesOrderId, "editor")).IsFailure.Should().BeTrue();
        (await bridge.ConfirmAsync(salesOrderId, "editor")).IsFailure.Should().BeTrue();
        await database.ExecuteAsync(
            "UPDATE SLS_SALES_ORDER SET IS_HOLD='N' WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId });
        (await bridge.ConfirmAsync(salesOrderId, "editor")).IsSuccess.Should().BeTrue();
        (await bridge.CloseAsync(salesOrderId, "editor")).IsFailure.Should().BeTrue();
        await database.ExecuteAsync(
            "UPDATE SLS_SALES_ORDER SET STATUS='Producing' WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId });
        (await bridge.CloseAsync(salesOrderId, "editor")).IsSuccess.Should().BeTrue();
        (await database.ScalarAsync<string>(
            "SELECT STATUS FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId })).Should().Be("Closed");

        var linkedOrderId = $"SLSL_{suffix}";
        (await bridge.SaveDraftAsync(draft with { SalesOrderId = linkedOrderId }))
            .IsSuccess.Should().BeTrue();
        var salesRequestId = $"SLSR_{suffix}";
        await database.ExecuteAsync("""
            INSERT INTO SLS_SALES_REQUEST (SALES_REQUEST_ID, SALES_ORDER_ID, STATUS)
            VALUES (@salesRequestId, @linkedOrderId, 'Confirmed')
            """, new { salesRequestId, linkedOrderId });
        (await bridge.DeleteDraftAsync(linkedOrderId, "editor")).IsFailure.Should().BeTrue();
        await database.ExecuteAsync(
            "DELETE FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID=@salesRequestId",
            new { salesRequestId });
        (await bridge.DeleteDraftAsync(linkedOrderId, "editor")).IsSuccess.Should().BeTrue();
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@linkedOrderId",
            new { linkedOrderId })).Should().Be(0);
    }
}
