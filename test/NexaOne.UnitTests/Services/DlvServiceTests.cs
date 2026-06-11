using Moq;
using NexaOne.DLV.Application.Dlv;
using NexaOne.DLV.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class DlvServiceTests
{
    private static readonly DateTime Requested = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    private static DeliveryOrder DraftOrder(string id = "DO001") =>
        DeliveryOrder.Create(id, "삼성전자", "PLANT01", Requested).Value;

    private static DeliveryItem TestItem(string id = "ITEM001") =>
        DeliveryItem.Create(id, "DO001", "PROD001", 100m).Value;

    private DlvService BuildService(
        Mock<IDeliveryOrderRepository> orderRepo,
        Mock<IDeliveryItemRepository> itemRepo,
        Mock<IShipmentHistoryRepository> histRepo) =>
        new(orderRepo.Object, itemRepo.Object, histRepo.Object);

    // ── CreateOrderAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_valid_data_succeeds()
    {
        var orderRepo = new Mock<IDeliveryOrderRepository>();
        orderRepo.Setup(r => r.AddAsync(It.IsAny<DeliveryOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(orderRepo, new(), new())
            .CreateOrderAsync("DO001", "삼성전자", "PLANT01", Requested);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(DeliveryOrderStatus.Draft);
        orderRepo.Verify(r => r.AddAsync(It.IsAny<DeliveryOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateOrder_empty_customer_fails()
    {
        var result = await BuildService(new(), new(), new())
            .CreateOrderAsync("DO001", "", "PLANT01", Requested);
        result.IsFailure.Should().BeTrue();
    }

    // ── ConfirmOrderAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmOrder_from_Draft_transitions_to_Confirmed()
    {
        var order = DraftOrder();
        var orderRepo = new Mock<IDeliveryOrderRepository>();
        orderRepo.Setup(r => r.GetByIdAsync("DO001", default)).ReturnsAsync(order);
        orderRepo.Setup(r => r.UpdateAsync(order, default)).Returns(Task.CompletedTask);

        var result = await BuildService(orderRepo, new(), new()).ConfirmOrderAsync("DO001");

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(DeliveryOrderStatus.Confirmed);
    }

    [Fact]
    public async Task ConfirmOrder_not_found_fails()
    {
        var orderRepo = new Mock<IDeliveryOrderRepository>();
        orderRepo.Setup(r => r.GetByIdAsync("DO999", default)).ReturnsAsync((DeliveryOrder?)null);

        var result = await BuildService(orderRepo, new(), new()).ConfirmOrderAsync("DO999");
        result.IsFailure.Should().BeTrue();
    }

    // ── ShipOrderAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ShipOrder_from_Confirmed_succeeds()
    {
        var order = DraftOrder();
        order.Confirm();
        var orderRepo = new Mock<IDeliveryOrderRepository>();
        orderRepo.Setup(r => r.GetByIdAsync("DO001", default)).ReturnsAsync(order);
        orderRepo.Setup(r => r.UpdateAsync(order, default)).Returns(Task.CompletedTask);

        var result = await BuildService(orderRepo, new(), new())
            .ShipOrderAsync("DO001", DateTime.UtcNow);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(DeliveryOrderStatus.Shipped);
    }

    [Fact]
    public async Task ShipOrder_from_Draft_fails()
    {
        var order = DraftOrder();
        var orderRepo = new Mock<IDeliveryOrderRepository>();
        orderRepo.Setup(r => r.GetByIdAsync("DO001", default)).ReturnsAsync(order);

        var result = await BuildService(orderRepo, new(), new())
            .ShipOrderAsync("DO001", DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
    }

    // ── AddItemAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddItem_valid_data_succeeds()
    {
        var itemRepo = new Mock<IDeliveryItemRepository>();
        itemRepo.Setup(r => r.AddAsync(It.IsAny<DeliveryItem>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(new(), itemRepo, new())
            .AddItemAsync("ITEM001", "DO001", "PROD001", 100m);

        result.IsSuccess.Should().BeTrue();
        result.Value.PlannedQty.Should().Be(100m);
        itemRepo.Verify(r => r.AddAsync(It.IsAny<DeliveryItem>(), default), Times.Once);
    }

    [Fact]
    public async Task AddItem_zero_qty_fails()
    {
        var result = await BuildService(new(), new(), new())
            .AddItemAsync("ITEM001", "DO001", "PROD001", 0m);
        result.IsFailure.Should().BeTrue();
    }

    // ── SetItemActualQtyAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task SetActualQty_updates_item()
    {
        var item = TestItem();
        var itemRepo = new Mock<IDeliveryItemRepository>();
        itemRepo.Setup(r => r.GetByIdAsync("ITEM001", default)).ReturnsAsync(item);
        itemRepo.Setup(r => r.UpdateAsync(item, default)).Returns(Task.CompletedTask);

        var result = await BuildService(new(), itemRepo, new())
            .SetItemActualQtyAsync("ITEM001", 95m);

        result.IsSuccess.Should().BeTrue();
        item.ActualQty.Should().Be(95m);
    }

    [Fact]
    public async Task SetActualQty_negative_value_fails()
    {
        var item = TestItem();
        var itemRepo = new Mock<IDeliveryItemRepository>();
        itemRepo.Setup(r => r.GetByIdAsync("ITEM001", default)).ReturnsAsync(item);

        var result = await BuildService(new(), itemRepo, new())
            .SetItemActualQtyAsync("ITEM001", -5m);

        result.IsFailure.Should().BeTrue();
    }

    // ── RecordShipmentAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RecordShipment_valid_data_succeeds()
    {
        var histRepo = new Mock<IShipmentHistoryRepository>();
        histRepo.Setup(r => r.AddAsync(It.IsAny<ShipmentHistory>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(new(), new(), histRepo)
            .RecordShipmentAsync("SH001", "DO001", 100m, "user01", "CJ대한통운", "1234567890");

        result.IsSuccess.Should().BeTrue();
        result.Value.ShippedQty.Should().Be(100m);
        result.Value.Carrier.Should().Be("CJ대한통운");
        histRepo.Verify(r => r.AddAsync(It.IsAny<ShipmentHistory>(), default), Times.Once);
    }

    [Fact]
    public async Task RecordShipment_zero_qty_fails()
    {
        var result = await BuildService(new(), new(), new())
            .RecordShipmentAsync("SH001", "DO001", 0m, "user01");
        result.IsFailure.Should().BeTrue();
    }
}
