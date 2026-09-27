using FluentAssertions;
using NexaOne.MDM.Infrastructure;
using NexaOne.SHP.Infrastructure;
using NexaOne.ServiceContracts.Sls;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>실제 MSSQL 스키마에서 SLS→SHP 트랜잭션 인계와 실패 롤백을 검증합니다.</summary>
[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlSlsSalesOrderDeliveryContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Delivery_request_creates_shipment_and_item_but_item_conflict_rolls_back_both_writes()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return; // 로컬 soft skip, 전용 CI에서는 연결 필수

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var plant = $"SLSP_{suffix}";
        var customer = $"SLSC_{suffix}";
        var product = $"SLSI_{suffix}";
        var firstSalesOrder = $"SLSO_{suffix}";
        var secondSalesOrder = $"SLSO2_{suffix}";
        var deliveryOrder = $"SLSD_{suffix}";
        var deliveryItem = $"SLSDI_{suffix}";
        var rolledBackOrder = $"SLSD2_{suffix}";
        const string actor = "sls-delivery-contract";
        await database.ExecuteAsync("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@plant, @plant);
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
                VALUES (@customer, @customer, 1);
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES (@product, @product, 'FinishedGoods', 'EA', 'Valid');
            INSERT INTO SLS_SALES_ORDER
                (SALES_ORDER_ID, PLANT_ID, CUSTOMER_ID, PRODUCT_ID, PLAN_END_DATE, PLAN_QTY, STATUS)
                VALUES (@firstSalesOrder, @plant, @customer, @product, '2040-09-30', 12.5, 'Draft');
            INSERT INTO SLS_SALES_ORDER
                (SALES_ORDER_ID, PLANT_ID, CUSTOMER_ID, PRODUCT_ID, PLAN_END_DATE, PLAN_QTY, STATUS)
                VALUES (@secondSalesOrder, @plant, @customer, @product, '2040-09-30', 12.5, 'Draft');
            """, new { plant, customer, product, firstSalesOrder, secondSalesOrder });

        var bridge = new NexaOne.SLS.Module(
            database.DataSource, new BusinessMasterDirectory(database.DataSource),
            new SalesOrderShipmentIntake(), new SalesOrderShipmentEvidence()).GetSalesOrderDeliveryBridge();
        var requested = await bridge.RequestDeliveryAsync(new SalesOrderDeliveryCommand(
            firstSalesOrder, deliveryOrder, deliveryItem, actor));
        requested.IsSuccess.Should().BeTrue(requested.Error.Description);
        requested.Value.Should().Be(new SalesOrderDeliveryState(firstSalesOrder, deliveryOrder, "Confirmed"));
        (await database.ScalarAsync<int>("""
            SELECT COUNT(1) FROM SLS_SALES_ORDER
             WHERE SALES_ORDER_ID=@firstSalesOrder AND STATUS='Confirmed'
               AND DELIVERY_ORDER_ID=@deliveryOrder AND UPDATED_BY=@actor
            """, new { firstSalesOrder, deliveryOrder, actor })).Should().Be(1);
        (await database.ScalarAsync<int>("""
            SELECT COUNT(1) FROM SHP_DELIVERY_ORDER
             WHERE ORDER_ID=@deliveryOrder AND CUSTOMER_NAME=@customer
               AND PLANT_ID=@plant AND STATUS='Draft' AND CREATED_BY=@actor
            """, new { deliveryOrder, customer, plant, actor })).Should().Be(1);
        (await database.ScalarAsync<int>("""
            SELECT COUNT(1) FROM SHP_DELIVERY_ITEM
             WHERE ITEM_ID=@deliveryItem AND DELIVERY_ORDER_ID=@deliveryOrder
               AND PRODUCT_ID=@product AND PLANNED_QTY=12.5 AND CREATED_BY=@actor
            """, new { deliveryItem, deliveryOrder, product, actor })).Should().Be(1);

        var notShipped = await bridge.ConfirmDeliveryAsync(new SalesOrderDeliveryConfirmationCommand(
            firstSalesOrder, actor));
        notShipped.IsFailure.Should().BeTrue();
        notShipped.Error.Code.Should().Be("SLS_SHIPMENT_NOT_SHIPPED");
        await database.ExecuteAsync("""
            UPDATE SHP_DELIVERY_ORDER SET STATUS='Shipped', SHIPPED_DATE='2040-09-30'
             WHERE ORDER_ID=@deliveryOrder
            """, new { deliveryOrder });
        await database.ExecuteAsync(
            "UPDATE SHP_DELIVERY_ITEM SET ACTUAL_QTY=11 WHERE ITEM_ID=@deliveryItem",
            new { deliveryItem });
        var shortShipment = await bridge.ConfirmDeliveryAsync(new SalesOrderDeliveryConfirmationCommand(
            firstSalesOrder, actor));
        shortShipment.IsFailure.Should().BeTrue();
        shortShipment.Error.Code.Should().Be("SLS_SHIPMENT_MISMATCH");
        await database.ExecuteAsync(
            "UPDATE SHP_DELIVERY_ITEM SET ACTUAL_QTY=12.5 WHERE ITEM_ID=@deliveryItem",
            new { deliveryItem });
        var delivered = await bridge.ConfirmDeliveryAsync(new SalesOrderDeliveryConfirmationCommand(
            firstSalesOrder, actor));
        delivered.IsSuccess.Should().BeTrue(delivered.Error.Description);
        delivered.Value.Should().Be(new SalesOrderDeliveryConfirmationState(
            firstSalesOrder, deliveryOrder, "Delivered", 12.5m));
        (await database.ScalarAsync<int>("""
            SELECT COUNT(1) FROM SLS_SALES_ORDER
             WHERE SALES_ORDER_ID=@firstSalesOrder AND STATUS='Delivered'
               AND DELIVERED_QTY=12.5 AND UPDATED_BY=@actor
            """, new { firstSalesOrder, actor })).Should().Be(1);
        (await bridge.ConfirmDeliveryAsync(new SalesOrderDeliveryConfirmationCommand(
            firstSalesOrder, actor))).IsSuccess.Should().BeTrue("confirmation replay is idempotent");

        var conflict = await bridge.RequestDeliveryAsync(new SalesOrderDeliveryCommand(
            secondSalesOrder, rolledBackOrder, deliveryItem, actor));
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("SLS_DELIVERY_ID_CONFLICT");
        (await database.ScalarAsync<int>("""
            SELECT COUNT(1) FROM SLS_SALES_ORDER
             WHERE SALES_ORDER_ID=@secondSalesOrder AND STATUS='Draft' AND DELIVERY_ORDER_ID IS NULL
            """, new { secondSalesOrder })).Should().Be(1, "SLS 연결도 롤백되어야 한다");
        (await database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM SHP_DELIVERY_ORDER WHERE ORDER_ID=@rolledBackOrder",
            new { rolledBackOrder })).Should().Be(0, "SHP 주문 삽입도 롤백되어야 한다");

        var replay = await bridge.RequestDeliveryAsync(new SalesOrderDeliveryCommand(
            firstSalesOrder, deliveryOrder, deliveryItem, actor));
        replay.IsFailure.Should().BeTrue();
        replay.Error.Code.Should().Be("SLS_DELIVERY_NOT_REQUESTABLE");
    }
}
