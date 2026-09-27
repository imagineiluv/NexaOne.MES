using FluentAssertions;
using NexaOne.Application.Query;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlSlsDraftGuardContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Named_sales_order_commands_do_not_edit_or_delete_a_held_draft()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var plant = $"SLSP_{suffix}";
        var customer = $"SLSC_{suffix}";
        var product = $"SLSI_{suffix}";
        var salesOrderId = $"SLSGD_{suffix}";
        await database.ExecuteAsync("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@plant, @plant);
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
                VALUES (@customer, @customer, 1);
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES (@product, @product, 'FinishedGoods', 'EA', 'Valid');
            """, new { plant, customer, product });

        var registry = FileQueryRegistry.Load("mssql",
            RepositorySource.GetDirectory("src/00.Main/NexaOne.Server/config/db/queries"));
        var parameters = new Dictionary<string, object?>
        {
            ["salesOrderId"] = salesOrderId,
            ["plantId"] = plant,
            ["salesOrderName"] = "original",
            ["customerId"] = customer,
            ["productId"] = product,
            ["planStartDate"] = new DateTime(2040, 9, 1),
            ["planEndDate"] = new DateTime(2040, 9, 30),
            ["planQty"] = 12.5m,
            ["currentUser"] = "sls-draft-contract",
        };

        async Task Command(string queryId)
        {
            registry.TryGet(queryId, out var definition).Should().BeTrue();
            definition.Should().NotBeNull();
            parameters["utcNow"] = DateTime.UtcNow;
            await database.ExecuteAsync(definition!.Sql, parameters);
        }

        Task<string> Name() => database.ScalarAsync<string>(
            "SELECT SALES_ORDER_NAME FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId });

        await Command("SLS.CreateSalesOrder");
        parameters["salesOrderName"] = "edited draft";
        await Command("SLS.CreateSalesOrder");
        (await Name()).Should().Be("edited draft");

        await database.ExecuteAsync(
            "UPDATE SLS_SALES_ORDER SET IS_HOLD='Y' WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId });
        parameters["salesOrderName"] = "illegal edit";
        await Command("SLS.CreateSalesOrder");
        await Command("SLS.DeleteSalesOrder");
        (await Name()).Should().Be("edited draft", "held Draft orders cannot be edited or deleted");
        (await database.ScalarAsync<string>(
            "SELECT IS_HOLD FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId })).Should().Be("Y");

        await database.ExecuteAsync(
            "UPDATE SLS_SALES_ORDER SET IS_HOLD='N' WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId });
        parameters["salesOrderName"] = "released draft";
        await Command("SLS.CreateSalesOrder");
        (await Name()).Should().Be("released draft");

        var salesRequestId = $"SLSR_{suffix}";
        await database.ExecuteAsync("""
            INSERT INTO SLS_SALES_REQUEST (SALES_REQUEST_ID, SALES_ORDER_ID, STATUS)
            VALUES (@salesRequestId, @salesOrderId, 'Confirmed')
            """, new { salesRequestId, salesOrderId });
        await Command("SLS.DeleteSalesOrder");
        (await Name()).Should().Be("released draft", "a linked sales request must not be orphaned");
        await database.ExecuteAsync(
            "DELETE FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID=@salesRequestId",
            new { salesRequestId });
        await Command("SLS.DeleteSalesOrder");
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
            new { salesOrderId })).Should().Be(0, "unheld Draft orders remain deletable");
    }
}
