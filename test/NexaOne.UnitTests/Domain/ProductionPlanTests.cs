using NexaOne.PPM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class ProductionPlanTests
{
    private static readonly DateTime Start = new(2026, 2, 1);
    private static readonly DateTime End   = new(2026, 2, 28);

    private static ProductionPlan Draft() =>
        ProductionPlan.Create("PP001", "Feb Plan", "P01", "PROD-A", 1000m, Start, End).Value;

    [Fact]
    public void Create_plan_starts_in_Draft()
    {
        var p = Draft();
        p.Status.Should().Be(ProductionPlanStatus.Draft);
        p.PlannedQty.Should().Be(1000m);
    }

    [Fact]
    public void Create_with_zero_qty_fails()
    {
        ProductionPlan.Create("PP002", "name", "P01", "PROD-A", 0, Start, End)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_with_start_after_end_fails()
    {
        ProductionPlan.Create("PP003", "name", "P01", "PROD-A", 100m, End, Start)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Release_moves_to_Released()
    {
        var p = Draft();
        p.Release().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.Released);
    }

    [Fact]
    public void Release_from_non_Draft_fails()
    {
        var p = Draft();
        p.Release();
        p.Release().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_requires_Released()
    {
        var p = Draft();
        p.Start().IsFailure.Should().BeTrue();
        p.Release();
        p.Start().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.InProgress);
    }

    [Fact]
    public void Complete_requires_InProgress()
    {
        var p = Draft();
        p.Release();
        p.Complete().IsFailure.Should().BeTrue();
        p.Start();
        p.Complete().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.Completed);
    }

    [Fact]
    public void Cancel_from_Completed_fails()
    {
        var p = Draft();
        p.Release();
        p.Start();
        p.Complete();
        p.Cancel().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Draft_succeeds()
    {
        var p = Draft();
        p.Cancel().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.Cancelled);
    }
}
