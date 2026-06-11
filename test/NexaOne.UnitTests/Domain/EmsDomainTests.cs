using NexaOne.EMS.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class EmsDomainTests
{
    private static readonly DateTime Scheduled = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── MaintenancePlan ───────────────────────────────────────────────────────

    [Fact]
    public void Create_plan_valid_succeeds()
    {
        var result = MaintenancePlan.Create("MP001", "월간 PM", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01");
        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(MaintenancePlanStatus.Planned);
    }

    [Theory]
    [InlineData("PM")]
    [InlineData("CM")]
    public void Create_plan_valid_plan_types(string planType)
    {
        var result = MaintenancePlan.Create("MP001", "계획", "EQ001", planType, "Monthly", Scheduled, 4m, "tech01");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_plan_invalid_plan_type_fails()
    {
        var result = MaintenancePlan.Create("MP001", "계획", "EQ001", "INVALID", "Monthly", Scheduled, 4m, "tech01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_plan_zero_duration_fails()
    {
        var result = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 0m, "tech01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_plan_from_Planned_transitions_to_InProgress()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        var result = plan.Start();
        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.InProgress);
    }

    [Fact]
    public void Complete_plan_from_InProgress_transitions_to_Completed()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        plan.Start();
        var result = plan.Complete();
        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Completed);
    }

    [Fact]
    public void Complete_plan_from_Planned_fails()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        var result = plan.Complete();
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_plan_from_Planned_succeeds()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        var result = plan.Cancel();
        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Cancelled);
    }

    [Fact]
    public void Cancel_plan_from_Completed_fails()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        plan.Start();
        plan.Complete();
        var result = plan.Cancel();
        result.IsFailure.Should().BeTrue();
    }

    // ── SparePart ─────────────────────────────────────────────────────────────

    [Fact]
    public void Create_spare_part_valid_succeeds()
    {
        var result = SparePart.Create("SP001", "오일 필터", "P-001", "설명", "EA", 10m, 3m, 50m, "A-01");
        result.IsSuccess.Should().BeTrue();
        result.Value.IsLowStock.Should().BeFalse();
    }

    [Fact]
    public void Create_spare_part_max_stock_less_than_min_fails()
    {
        var result = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 5m, 10m, 5m, "A-01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_spare_part_negative_stock_fails()
    {
        var result = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", -1m, 3m, 50m, "A-01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AdjustStock_increase_succeeds()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 10m, 3m, 50m, "A-01").Value;
        var result = part.AdjustStock(5m);
        result.IsSuccess.Should().BeTrue();
        part.CurrentStock.Should().Be(15m);
    }

    [Fact]
    public void AdjustStock_decrease_succeeds()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 10m, 3m, 50m, "A-01").Value;
        var result = part.AdjustStock(-7m);
        result.IsSuccess.Should().BeTrue();
        part.CurrentStock.Should().Be(3m);
    }

    [Fact]
    public void AdjustStock_below_zero_fails()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 5m, 3m, 50m, "A-01").Value;
        var result = part.AdjustStock(-10m);
        result.IsFailure.Should().BeTrue();
        part.CurrentStock.Should().Be(5m);
    }

    [Fact]
    public void IsLowStock_true_when_current_stock_at_or_below_min()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 3m, 3m, 50m, "A-01").Value;
        part.IsLowStock.Should().BeTrue();
    }
}
