using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

public sealed class SlsSalesOrderMetaCommandDriverTests
{
    private static readonly MetaCommandExecutionContext Context =
        new("FACTORY_SLS_SALES_ORDER", "MES");

    [Fact]
    public async Task Save_maps_form_fields_to_typed_sls_request()
    {
        SlsSalesOrderDraftRequest? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(client => client.SaveSlsSalesOrderAsync(
                It.IsAny<SlsSalesOrderDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SlsSalesOrderDraftRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new SlsSalesOrderActionResult(true, null, 200));
        var driver = new SlsSalesOrderMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(SlsSalesOrderMetaCommands.Save,
            new Dictionary<string, object?>
            {
                ["salesOrderId"] = "SO-1", ["salesOrderName"] = "첫 수주",
                ["plantId"] = "P-1", ["customerId"] = "C-1", ["productId"] = "I-1",
                ["planStartDate"] = "2040-09-01", ["planEndDate"] = "2040-09-30",
                ["planQty"] = "12.5",
            }, Context);

        result.Success.Should().BeTrue();
        captured.Should().Be(new SlsSalesOrderDraftRequest(
            "SO-1", "첫 수주", "P-1", "C-1", "I-1",
            new DateTime(2040, 9, 1), new DateTime(2040, 9, 30), 12.5m));
        driver.Commands.Should().OnlyContain(command => command.RequiredPermission == "sls:manage");
    }

    [Theory]
    [InlineData(SlsSalesOrderMetaCommands.Delete, "delete", "Draft")]
    [InlineData(SlsSalesOrderMetaCommands.Confirm, "confirm", "Draft")]
    [InlineData("BRIDGE:SLS.SALES-ORDER.CONFIRM", "confirm", "Draft")]
    [InlineData(SlsSalesOrderMetaCommands.Close, "close", "Delivered")]
    public async Task Row_action_maps_to_typed_sls_endpoint(
        string commandId, string expectedAction, string status)
    {
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecuteSlsSalesOrderActionAsync(
                expectedAction, "SO-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlsSalesOrderActionResult(true, null, 204));
        var driver = new SlsSalesOrderMetaCommandDriver(api.Object);
        var row = new Dictionary<string, object?>
        {
            ["SALES_ORDER_ID"] = "SO-1", ["STATUS"] = status, ["IS_HOLD"] = "N",
        };

        (await driver.ExecuteAsync(commandId, row, Context)).Success.Should().BeTrue();
        api.Verify(client => client.ExecuteSlsSalesOrderActionAsync(
            expectedAction, "SO-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Held_or_wrong_source_row_is_blocked_before_the_api_call()
    {
        var api = new Mock<IApiClient>();
        var driver = new SlsSalesOrderMetaCommandDriver(api.Object);
        var held = new Dictionary<string, object?>
        {
            ["SALES_ORDER_ID"] = "SO-1", ["STATUS"] = "Draft", ["IS_HOLD"] = "Y",
        };
        var wrongState = new Dictionary<string, object?>
        {
            ["SALES_ORDER_ID"] = "SO-1", ["STATUS"] = "Confirmed", ["IS_HOLD"] = "N",
        };

        (await driver.ExecuteAsync(SlsSalesOrderMetaCommands.Delete, held, Context))
            .Success.Should().BeFalse();
        (await driver.ExecuteAsync(SlsSalesOrderMetaCommands.Confirm, wrongState, Context))
            .Success.Should().BeFalse();
        api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Server_conflict_status_and_reason_are_preserved()
    {
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecuteSlsSalesOrderActionAsync(
                "confirm", "SO-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlsSalesOrderActionResult(false, "보류된 수주입니다.", 409));
        var driver = new SlsSalesOrderMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(SlsSalesOrderMetaCommands.Confirm,
            new Dictionary<string, object?> { ["SALES_ORDER_ID"] = "SO-1", ["STATUS"] = "Draft" },
            Context);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("보류된 수주입니다.");
    }
}
