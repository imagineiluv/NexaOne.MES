using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

public sealed class PrcPurchaseOrderMetaCommandDriverTests
{
    private static readonly MetaCommandExecutionContext Context =
        new("FACTORY_PRC_PURCHASE_ORDER", "MES");

    [Theory]
    [InlineData(PrcPurchaseOrderMetaCommands.Order, "order", "Draft")]
    [InlineData("BRIDGE:PRC.PURCHASE-ORDER.ORDER", "order", "Draft")]
    [InlineData(PrcPurchaseOrderMetaCommands.Close, "close", "Incoming")]
    public async Task Row_action_calls_the_typed_prc_api(
        string commandId, string expectedAction, string status)
    {
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecutePrcPurchaseOrderActionAsync(
                expectedAction, "PO-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrcPurchaseOrderActionResult(true, null, 200));
        var driver = new PrcPurchaseOrderMetaCommandDriver(api.Object);
        var row = new Dictionary<string, object?>
        {
            ["PURCHASE_ORDER_ID"] = "PO-1", ["STATUS"] = status, ["IS_HOLD"] = "N",
        };

        (await driver.ExecuteAsync(commandId, row, Context)).Success.Should().BeTrue();
        driver.Commands.Should().OnlyContain(command => command.RequiredPermission == "prc:manage");
        api.Verify(client => client.ExecutePrcPurchaseOrderActionAsync(
            expectedAction, "PO-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Held_or_wrong_source_row_is_blocked_before_api_call()
    {
        var api = new Mock<IApiClient>();
        var driver = new PrcPurchaseOrderMetaCommandDriver(api.Object);
        var held = new Dictionary<string, object?>
        {
            ["PURCHASE_ORDER_ID"] = "PO-1", ["STATUS"] = "Draft", ["IS_HOLD"] = "Y",
        };
        var wrongState = new Dictionary<string, object?>
        {
            ["PURCHASE_ORDER_ID"] = "PO-1", ["STATUS"] = "Ordered", ["IS_HOLD"] = "N",
        };

        (await driver.ExecuteAsync(PrcPurchaseOrderMetaCommands.Order, held, Context))
            .Success.Should().BeFalse();
        (await driver.ExecuteAsync(PrcPurchaseOrderMetaCommands.Close, wrongState, Context))
            .Success.Should().BeFalse();
        api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Server_conflict_status_and_reason_are_preserved()
    {
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecutePrcPurchaseOrderActionAsync(
                "close", "PO-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrcPurchaseOrderActionResult(false, "입고 수량이 부족합니다.", 409));
        var driver = new PrcPurchaseOrderMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(PrcPurchaseOrderMetaCommands.Close,
            new Dictionary<string, object?> { ["PURCHASE_ORDER_ID"] = "PO-1", ["STATUS"] = "Incoming" },
            Context);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("입고 수량이 부족합니다.");
    }
}
