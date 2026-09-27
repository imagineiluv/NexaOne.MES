using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

public sealed class PrcPurchaseOrderItemsTests
{
    [Fact]
    public void Purchase_order_screen_opens_items_for_selected_row_and_clears_on_reset()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var definition = new ScreenDefinition("FACTORY_PRC_PURCHASE_ORDER", "구매오더 관리",
            new FieldDefinition[] { new("purchaseOrderId", "발주 ID", Required: true) },
            new GridColumnDefinition[] { new("PURCHASE_ORDER_ID", "발주 ID") },
            QueryId: "PRC.PurchaseOrderList", SaveQueryId: "PRC.CreatePurchaseOrder",
            Purpose: ScreenPurpose.Manage);
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.GetAsync(definition.UiId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecuteQueryAsync("PRC.PurchaseOrderList",
                It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object?>>
            {
                new() { ["PURCHASE_ORDER_ID"] = "PO-1", ["STATUS"] = "Draft", ["IS_HOLD"] = "N" },
            });
        api.Setup(client => client.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<PrcPurchaseOrderItemDto> { new("P-1", 2m, 0m) }, 200, null, null));
        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.Render<MetaScreen>(parameters =>
            parameters.Add(component => component.UiId, definition.UiId));
        cut.FindComponent<PrcPurchaseOrderItems>().Instance.PurchaseOrderId.Should().BeNull();

        var grid = cut.FindComponent<MetaGridRenderer>();
        cut.InvokeAsync(() => grid.Instance.OnRowSelect.InvokeAsync(new Dictionary<string, object?>
        {
            ["PURCHASE_ORDER_ID"] = "PO-1", ["STATUS"] = "Draft", ["IS_HOLD"] = "N",
        }));

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<PrcPurchaseOrderItems>().Instance.PurchaseOrderId.Should().Be("PO-1");
            cut.Markup.Should().Contain("P-1");
        });
        cut.Find("button.meta-editor-reset").Click();
        cut.WaitForAssertion(() =>
            cut.FindComponent<PrcPurchaseOrderItems>().Instance.PurchaseOrderId.Should().BeNull());
    }

    [Fact]
    public void Selection_loads_items_and_draft_item_save_reloads_server_state()
    {
        using var ctx = new BunitContext();
        var api = new Mock<IApiClient>();
        var rows = new List<PrcPurchaseOrderItemDto>
        {
            new("P-1", 2m, 0m),
        };
        api.Setup(client => client.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                "api/v1/prc/purchase-orders/PO-1/items", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (rows.ToList(), 200, null, null));
        api.Setup(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Put, "api/v1/prc/purchase-orders/PO-1/items/P-2",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                rows.Add(new PrcPurchaseOrderItemDto("P-2", 3.5m, 0m));
                return (new PrcPurchaseOrderItemTotalDto("PO-1", 5.5m), 200, null, null);
            });
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.Render<PrcPurchaseOrderItems>(parameters => parameters
            .Add(component => component.PurchaseOrderId, "PO-1")
            .Add(component => component.OrderStatus, "Draft")
            .Add(component => component.CanManage, true)
            .Add(component => component.UserId, "operator"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("P-1"));
        cut.Find("input[type=text]").Change("P-2");
        cut.Find("input[type=number]").Change("3.5");
        cut.Find("button.prc-items-save").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("P-2").And.Contain("합계 5.5000");
            api.Verify(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Put, "api/v1/prc/purchase-orders/PO-1/items/P-2",
                It.Is<object>(body => (decimal)body.GetType().GetProperty("quantity")!.GetValue(body)! == 3.5m),
                "operator", It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Fact]
    public void Held_order_and_received_item_are_read_only()
    {
        using var ctx = new BunitContext();
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<PrcPurchaseOrderItemDto> { new("P-1", 2m, 1m) }, 200, null, null));
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.Render<PrcPurchaseOrderItems>(parameters => parameters
            .Add(component => component.PurchaseOrderId, "PO-1")
            .Add(component => component.OrderStatus, "Draft")
            .Add(component => component.IsHeld, true)
            .Add(component => component.CanManage, true)
            .Add(component => component.UserId, "operator"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("보류된 발주").And.Contain("입고 기록으로 잠김");
            cut.FindAll("button.prc-items-save").Should().BeEmpty();
        });
    }

    [Fact]
    public void Delete_requires_explicit_confirmation_and_reloads_the_remaining_items()
    {
        using var ctx = new BunitContext();
        var api = new Mock<IApiClient>();
        var rows = new List<PrcPurchaseOrderItemDto> { new("P-1", 2m, 0m) };
        api.Setup(client => client.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (rows.ToList(), 200, null, null));
        api.Setup(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Delete, "api/v1/prc/purchase-orders/PO-1/items/P-1",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                rows.Clear();
                return (new PrcPurchaseOrderItemTotalDto("PO-1", 0m), 200, null, null);
            });
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.Render<PrcPurchaseOrderItems>(parameters => parameters
            .Add(component => component.PurchaseOrderId, "PO-1")
            .Add(component => component.OrderStatus, "Draft")
            .Add(component => component.CanManage, true)
            .Add(component => component.UserId, "operator"));

        cut.WaitForAssertion(() => cut.FindAll("button.prc-items-delete").Should().ContainSingle());
        cut.Find("button.prc-items-delete").Click();
        api.Verify(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
            It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find(".prc-items-confirm button.prc-items-delete").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("등록된 품목이 없습니다");
            api.Verify(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Delete, "api/v1/prc/purchase-orders/PO-1/items/P-1",
                It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Fact]
    public void Uncertain_write_is_not_replayed_and_keeps_an_inspection_warning()
    {
        using var ctx = new BunitContext();
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<PrcPurchaseOrderItemDto>(), 200, null, null));
        api.Setup(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Put, It.IsAny<string>(), It.IsAny<object>(), "operator", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((PrcPurchaseOrderItemTotalDto?)null, 503,
                "INVENTORY_RESPONSE_UNAVAILABLE", "변경 결과를 확인할 수 없습니다."));
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.Render<PrcPurchaseOrderItems>(parameters => parameters
            .Add(component => component.PurchaseOrderId, "PO-1")
            .Add(component => component.OrderStatus, "Draft")
            .Add(component => component.CanManage, true)
            .Add(component => component.UserId, "operator"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("등록된 품목이 없습니다"));
        cut.Find("input[type=text]").Change("P-1");
        cut.Find("input[type=number]").Change("1");
        cut.Find("button.prc-items-save").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.Should().Contain("확인 전에는 같은 변경을 반복하지 마세요");
            api.Verify(client => client.WriteInventoryAsync<PrcPurchaseOrderItemTotalDto>(
                HttpMethod.Put, It.IsAny<string>(), It.IsAny<object>(), "operator",
                It.IsAny<CancellationToken>()), Times.Once);
            api.Verify(client => client.ReadInventoryAsync<List<PrcPurchaseOrderItemDto>>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        });
    }
}
