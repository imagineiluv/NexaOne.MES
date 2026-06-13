using Moq;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class CmmsServiceTests
{
    private static readonly DateTime Issued = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static WorkOrder IssuedWo(string id = "WO001") =>
        WorkOrder.Create(id, "EQ001", "PM", "Scheduled maintenance", "tech01", Issued).Value;

    private CmmsService BuildService(Mock<IWorkOrderRepository> repo) => new(repo.Object);

    // ── CreateWorkOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateWorkOrder_valid_data_succeeds()
    {
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<WorkOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateWorkOrderAsync("WO001", "EQ001", "PM", "Maintenance", "tech01");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(WorkOrderStatus.Issued);
        repo.Verify(r => r.AddAsync(It.IsAny<WorkOrder>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateWorkOrder_invalid_type_fails()
    {
        var repo = new Mock<IWorkOrderRepository>();
        var result = await BuildService(repo).CreateWorkOrderAsync("WO001", "EQ001", "INVALID", "desc", "tech01");
        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<WorkOrder>(), default), Times.Never);
    }

    // ── StartWorkOrderAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task StartWorkOrder_issued_wo_succeeds()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.UpdateAsync(It.IsAny<WorkOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).StartWorkOrderAsync("WO001");

        result.IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.InProgress);
    }

    [Fact]
    public async Task StartWorkOrder_not_found_returns_failure()
    {
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO999", default)).ReturnsAsync((WorkOrder?)null);

        var result = await BuildService(repo).StartWorkOrderAsync("WO999");

        result.IsFailure.Should().BeTrue();
    }

    // ── CompleteWorkOrderAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CompleteWorkOrder_in_progress_wo_succeeds()
    {
        var wo = IssuedWo();
        wo.Start();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.UpdateAsync(It.IsAny<WorkOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CompleteWorkOrderAsync("WO001", "All done");

        result.IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Completed);
    }

    [Fact]
    public async Task CompleteWorkOrder_issued_wo_fails()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);

        var result = await BuildService(repo).CompleteWorkOrderAsync("WO001", "");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<WorkOrder>(), default), Times.Never);
    }

    // ── CancelWorkOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CancelWorkOrder_issued_wo_succeeds()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.UpdateAsync(It.IsAny<WorkOrder>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CancelWorkOrderAsync("WO001");

        result.IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Cancelled);
    }

    [Fact]
    public async Task CancelWorkOrder_completed_wo_fails()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Complete();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);

        var result = await BuildService(repo).CancelWorkOrderAsync("WO001");

        result.IsFailure.Should().BeTrue();
    }
}
