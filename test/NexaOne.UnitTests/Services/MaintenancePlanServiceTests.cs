using Moq;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class MaintenancePlanServiceTests
{
    private static readonly DateTime Scheduled = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MaintenancePlan PlannedPlan(string id = "MP001") =>
        MaintenancePlan.Create(id, "월간 PM", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;

    private static SparePart TestPart(string id = "SP001") =>
        SparePart.Create(id, "오일 필터", "P-001", "엔진 오일 필터", "EA", 10m, 3m, 50m, "A-01", null).Value;

    private MaintenancePlanService BuildService(
        Mock<IMaintenancePlanRepository> planRepo,
        Mock<ISparePartRepository> partRepo) =>
        new(planRepo.Object, partRepo.Object);

    // ── CreatePlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlan_valid_data_succeeds()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.AddAsync(It.IsAny<MaintenancePlan>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(planRepo, partRepo)
            .CreatePlanAsync("MP001", "월간 PM", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(MaintenancePlanStatus.Planned);
        planRepo.Verify(r => r.AddAsync(It.IsAny<MaintenancePlan>(), default), Times.Once);
    }

    [Fact]
    public async Task CreatePlan_invalid_plan_type_fails()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();

        var result = await BuildService(planRepo, partRepo)
            .CreatePlanAsync("MP001", "월간 PM", "EQ001", "INVALID", "Monthly", Scheduled, 4m, "tech01");

        result.IsFailure.Should().BeTrue();
        planRepo.Verify(r => r.AddAsync(It.IsAny<MaintenancePlan>(), default), Times.Never);
    }

    // ── StartPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task StartPlan_transitions_to_InProgress()
    {
        var plan = PlannedPlan();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);
        planRepo.Setup(r => r.UpdateAsync(plan, default)).Returns(Task.CompletedTask);

        var result = await BuildService(planRepo, partRepo).StartPlanAsync("MP001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.InProgress);
        planRepo.Verify(r => r.UpdateAsync(plan, default), Times.Once);
    }

    [Fact]
    public async Task StartPlan_not_found_returns_failure()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP999", default)).ReturnsAsync((MaintenancePlan?)null);

        var result = await BuildService(planRepo, partRepo).StartPlanAsync("MP999");

        result.IsFailure.Should().BeTrue();
    }

    // ── CompletePlanAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CompletePlan_from_InProgress_succeeds()
    {
        var plan = PlannedPlan();
        plan.Start();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);
        planRepo.Setup(r => r.UpdateAsync(plan, default)).Returns(Task.CompletedTask);

        var result = await BuildService(planRepo, partRepo).CompletePlanAsync("MP001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Completed);
    }

    [Fact]
    public async Task CompletePlan_from_Planned_fails()
    {
        var plan = PlannedPlan();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);

        var result = await BuildService(planRepo, partRepo).CompletePlanAsync("MP001");

        result.IsFailure.Should().BeTrue();
        planRepo.Verify(r => r.UpdateAsync(It.IsAny<MaintenancePlan>(), default), Times.Never);
    }

    // ── CancelPlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPlan_from_Planned_succeeds()
    {
        var plan = PlannedPlan();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);
        planRepo.Setup(r => r.UpdateAsync(plan, default)).Returns(Task.CompletedTask);

        var result = await BuildService(planRepo, partRepo).CancelPlanAsync("MP001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Cancelled);
    }

    // ── CreatePartAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePart_valid_data_succeeds()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.AddAsync(It.IsAny<SparePart>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(planRepo, partRepo)
            .CreatePartAsync("SP001", "오일 필터", "P-001", "엔진 오일 필터", "EA", 10m, 3m, 50m, "A-01");

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStock.Should().Be(10m);
        partRepo.Verify(r => r.AddAsync(It.IsAny<SparePart>(), default), Times.Once);
    }

    // ── AdjustStockAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AdjustStock_increases_stock()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.UpdateAsync(part, default)).Returns(Task.CompletedTask);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync("SP001", 5m);

        result.IsSuccess.Should().BeTrue();
        part.CurrentStock.Should().Be(15m);
    }

    [Fact]
    public async Task AdjustStock_part_not_found_fails()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP999", default)).ReturnsAsync((SparePart?)null);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync("SP999", 5m);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AdjustStock_below_zero_fails()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync("SP001", -100m);

        result.IsFailure.Should().BeTrue();
        partRepo.Verify(r => r.UpdateAsync(It.IsAny<SparePart>(), default), Times.Never);
    }
}
