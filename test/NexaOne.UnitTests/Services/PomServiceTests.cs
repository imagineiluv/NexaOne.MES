using Moq;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class PomServiceTests
{
    private static readonly DateTime Start = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End   = new(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);

    private static ProductionPlan DraftPlan(string id = "PLAN001") =>
        ProductionPlan.Create(id, "Test Plan", "PLANT01", "PROD01", 100, Start, End).Value;

    private PomService BuildService(Mock<IProductionPlanRepository> repo) => new(repo.Object);

    // ── CreatePlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlan_valid_data_succeeds()
    {
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<ProductionPlan>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreatePlanAsync("PLAN001", "Test Plan", "PLANT01", "PROD01", 100, Start, End);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProductionPlanStatus.Draft);
        repo.Verify(r => r.AddAsync(It.IsAny<ProductionPlan>(), default), Times.Once);
    }

    [Fact]
    public async Task CreatePlan_zero_qty_fails()
    {
        var repo = new Mock<IProductionPlanRepository>();
        var result = await BuildService(repo).CreatePlanAsync("PLAN001", "Test Plan", "PLANT01", "PROD01", 0, Start, End);
        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<ProductionPlan>(), default), Times.Never);
    }

    [Fact]
    public async Task CreatePlan_end_before_start_fails()
    {
        var repo = new Mock<IProductionPlanRepository>();
        var result = await BuildService(repo).CreatePlanAsync("PLAN001", "Test Plan", "PLANT01", "PROD01", 100, End, Start);
        result.IsFailure.Should().BeTrue();
    }

    // ── ReleasePlanAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReleasePlan_draft_plan_succeeds()
    {
        var plan = DraftPlan();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionPlan>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).ReleasePlanAsync("PLAN001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(ProductionPlanStatus.Released);
    }

    [Fact]
    public async Task ReleasePlan_not_found_returns_failure()
    {
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN999", default)).ReturnsAsync((ProductionPlan?)null);

        var result = await BuildService(repo).ReleasePlanAsync("PLAN999");

        result.IsFailure.Should().BeTrue();
    }

    // ── StartPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task StartPlan_released_plan_succeeds()
    {
        var plan = DraftPlan();
        plan.Release();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionPlan>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).StartPlanAsync("PLAN001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(ProductionPlanStatus.InProgress);
    }

    [Fact]
    public async Task StartPlan_draft_plan_fails()
    {
        var plan = DraftPlan();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);

        var result = await BuildService(repo).StartPlanAsync("PLAN001");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<ProductionPlan>(), default), Times.Never);
    }

    [Fact]
    public async Task StartPlan_not_found_returns_failure()
    {
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN999", default)).ReturnsAsync((ProductionPlan?)null);

        var result = await BuildService(repo).StartPlanAsync("PLAN999");

        result.IsFailure.Should().BeTrue();
    }

    // ── CompletePlanAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CompletePlan_inprogress_plan_succeeds()
    {
        var plan = DraftPlan();
        plan.Release();
        plan.Start();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionPlan>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CompletePlanAsync("PLAN001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(ProductionPlanStatus.Completed);
    }

    [Fact]
    public async Task CompletePlan_released_plan_fails()
    {
        var plan = DraftPlan();
        plan.Release();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);

        var result = await BuildService(repo).CompletePlanAsync("PLAN001");

        result.IsFailure.Should().BeTrue();
    }

    // ── CancelPlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPlan_draft_plan_succeeds()
    {
        var plan = DraftPlan();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);
        repo.Setup(r => r.UpdateAsync(It.IsAny<ProductionPlan>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CancelPlanAsync("PLAN001");

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(ProductionPlanStatus.Cancelled);
    }

    [Fact]
    public async Task CancelPlan_completed_plan_fails()
    {
        var plan = DraftPlan();
        plan.Release();
        plan.Start();
        plan.Complete();
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN001", default)).ReturnsAsync(plan);

        var result = await BuildService(repo).CancelPlanAsync("PLAN001");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CancelPlan_not_found_returns_failure()
    {
        var repo = new Mock<IProductionPlanRepository>();
        repo.Setup(r => r.GetByIdAsync("PLAN999", default)).ReturnsAsync((ProductionPlan?)null);

        var result = await BuildService(repo).CancelPlanAsync("PLAN999");

        result.IsFailure.Should().BeTrue();
    }
}
