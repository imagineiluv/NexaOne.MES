using Moq;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class ProductionOrderServiceTests
{
    private static readonly DateTime SchedStart = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SchedEnd   = new(2026, 3, 1, 18, 0, 0, DateTimeKind.Utc);

    private static ProductionOrder IssuedOrder(string id = "ORD001") =>
        ProductionOrder.Create(id, "PLAN001", "EQ001", "PROD01", 100, SchedStart, SchedEnd).Value;

    private ProductionOrderService BuildService(Mock<IProductionOrderRepository> repo) => new(repo.Object);

    // ── CreateOrderAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_valid_data_succeeds()
    {
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<ProductionOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateOrderAsync(
            "ORD001", "PLAN001", "EQ001", "PROD01", 100, SchedStart, SchedEnd);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProductionOrderStatus.Issued);
        repo.Verify(r => r.AddAsync(It.IsAny<ProductionOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateOrder_zero_qty_fails()
    {
        var repo = new Mock<IProductionOrderRepository>();
        var result = await BuildService(repo).CreateOrderAsync(
            "ORD001", "PLAN001", "EQ001", "PROD01", 0, SchedStart, SchedEnd);

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<ProductionOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateOrder_end_before_start_fails()
    {
        var repo = new Mock<IProductionOrderRepository>();
        var result = await BuildService(repo).CreateOrderAsync(
            "ORD001", "PLAN001", "EQ001", "PROD01", 100, SchedEnd, SchedStart);

        result.IsFailure.Should().BeTrue();
    }

    // ── StartOrderAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task StartOrder_issued_order_succeeds()
    {
        var order = IssuedOrder();
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).StartOrderAsync("ORD001");

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(ProductionOrderStatus.InProgress);
        order.ActualStart.Should().NotBeNull();
    }

    [Fact]
    public async Task StartOrder_not_found_returns_failure()
    {
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("OXXX", default)).ReturnsAsync((ProductionOrder?)null);

        var result = await BuildService(repo).StartOrderAsync("OXXX");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task StartOrder_already_inprogress_fails()
    {
        var order = IssuedOrder();
        order.Start(DateTime.UtcNow);
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);

        var result = await BuildService(repo).StartOrderAsync("ORD001");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default), Times.Never);
    }

    // ── CompleteOrderAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteOrder_inprogress_order_succeeds()
    {
        var order = IssuedOrder();
        order.Start(DateTime.UtcNow);
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CompleteOrderAsync("ORD001", 95m);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(ProductionOrderStatus.Completed);
        order.ActualQty.Should().Be(95m);
        order.ActualEnd.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteOrder_issued_order_fails()
    {
        var order = IssuedOrder();
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);

        var result = await BuildService(repo).CompleteOrderAsync("ORD001", 100m);

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task CompleteOrder_negative_qty_fails()
    {
        var order = IssuedOrder();
        order.Start(DateTime.UtcNow);
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);

        var result = await BuildService(repo).CompleteOrderAsync("ORD001", -1m);

        result.IsFailure.Should().BeTrue();
    }

    // ── CancelOrderAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CancelOrder_issued_order_succeeds()
    {
        var order = IssuedOrder();
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CancelOrderAsync("ORD001");

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(ProductionOrderStatus.Cancelled);
    }

    [Fact]
    public async Task CancelOrder_completed_order_fails()
    {
        var order = IssuedOrder();
        order.Start(DateTime.UtcNow);
        order.Complete(100m, DateTime.UtcNow);
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("ORD001", default)).ReturnsAsync(order);

        var result = await BuildService(repo).CancelOrderAsync("ORD001");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CancelOrder_not_found_returns_failure()
    {
        var repo = new Mock<IProductionOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("OXXX", default)).ReturnsAsync((ProductionOrder?)null);

        var result = await BuildService(repo).CancelOrderAsync("OXXX");

        result.IsFailure.Should().BeTrue();
    }
}
