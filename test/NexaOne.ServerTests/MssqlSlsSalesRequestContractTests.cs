using FluentAssertions;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Sls;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>실제 MSSQL 마이그레이션 스키마에서 SLS 수령의 원자성을 검증합니다.</summary>
[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlSlsSalesRequestContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Receipt_links_one_request_to_one_draft_order_and_rolls_back_conflicting_order_id()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return; // 로컬 soft skip, 전용 CI에서는 연결 필수

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var plant = $"SLSP_{suffix}";
        var customer = $"SLSC_{suffix}";
        var product = $"SLSI_{suffix}";
        var requestId = $"SLSR_{suffix}";
        var secondRequestId = $"SLSR2_{suffix}";
        var orderId = $"SLSO_{suffix}";
        const string actor = "sls-mssql-contract";
        await database.ExecuteAsync("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@plant, @plant);
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE) VALUES (@customer, @customer, 1);
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES (@product, @product, 'FinishedGoods', 'EA', 'Valid');
            """, new { plant, customer, product });

        var bridge = new NexaOne.SLS.Module(
            database.DataSource, new BusinessMasterDirectory(database.DataSource)).GetSalesRequestBridge();
        foreach (var id in new[] { requestId, secondRequestId })
        {
            var created = await bridge.CreateDraftAsync(new SalesRequestDraftCommand(
                id, "MSSQL 판매 요청", customer, product, new DateTime(2040, 9, 1), 12.5m, actor));
            created.IsSuccess.Should().BeTrue(created.Error.Description);
        }

        var receipt = new SalesRequestReceiptCommand(
            requestId, orderId, plant, "MSSQL 수주", new DateTime(2040, 9, 1),
            new DateTime(2040, 9, 30), actor);
        var received = await bridge.ReceiveAsync(receipt);
        received.IsSuccess.Should().BeTrue(received.Error.Description);
        received.Value.Should().Be(new SalesRequestState(requestId, "Confirmed", orderId));
        (await database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@orderId " +
            "AND STATUS='Draft' AND CUSTOMER_ID=@customer AND PRODUCT_ID=@product " +
            "AND PLAN_QTY=12.5 AND CREATED_BY=@actor", new { orderId, customer, product, actor }))
            .Should().Be(1);

        var conflict = await bridge.ReceiveAsync(receipt with { SalesRequestId = secondRequestId });
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("SLS_ORDER_ID_CONFLICT");
        (await database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID=@secondRequestId " +
            "AND STATUS='Draft' AND SALES_ORDER_ID IS NULL", new { secondRequestId }))
            .Should().Be(1, "중복 수주 ID가 나면 요청 전이도 롤백되어야 한다");
    }
}
