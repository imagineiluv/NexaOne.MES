using NexaOne.PPM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class ProductionOrderTests
{
    private static readonly DateTime SchedStart = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SchedEnd   = new(2026, 3, 1, 18, 0, 0, DateTimeKind.Utc);

    private static ProductionOrder Issued() =>
        ProductionOrder.Create("ORD001", "PLAN001", "EQ001", "PROD01", 100m, SchedStart, SchedEnd).Value;

    [Fact]
    public void Create_order_starts_in_Issued()
    {
        var o = Issued();
        o.Status.Should().Be(ProductionOrderStatus.Issued);
        o.OrderQty.Should().Be(100m);
        o.ActualQty.Should().BeNull();
        o.ActualStart.Should().BeNull();
        o.ActualEnd.Should().BeNull();
    }

    [Fact]
    public void Create_with_zero_qty_fails()
    {
        ProductionOrder.Create("ORD001", "PLAN001", "EQ001", "PROD01", 0, SchedStart, SchedEnd)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_with_start_after_end_fails()
    {
        ProductionOrder.Create("ORD001", "PLAN001", "EQ001", "PROD01", 100m, SchedEnd, SchedStart)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_from_Issued_moves_to_InProgress()
    {
        var o = Issued();
        var actualStart = DateTime.UtcNow;
        o.Start(actualStart).IsSuccess.Should().BeTrue();
        o.Status.Should().Be(ProductionOrderStatus.InProgress);
        o.ActualStart.Should().Be(actualStart);
    }

    [Fact]
    public void Start_from_non_Issued_fails()
    {
        var o = Issued();
        o.Cancel();
        o.Start(DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Complete_from_InProgress_succeeds()
    {
        var o = Issued();
        o.Start(DateTime.UtcNow);
        var actualEnd = DateTime.UtcNow;
        o.Complete(95m, actualEnd).IsSuccess.Should().BeTrue();
        o.Status.Should().Be(ProductionOrderStatus.Completed);
        o.ActualQty.Should().Be(95m);
        o.ActualEnd.Should().Be(actualEnd);
    }

    [Fact]
    public void Complete_from_Issued_fails()
    {
        var o = Issued();
        o.Complete(100m, DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Complete_with_negative_qty_fails()
    {
        var o = Issued();
        o.Start(DateTime.UtcNow);
        o.Complete(-1m, DateTime.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Issued_succeeds()
    {
        var o = Issued();
        o.Cancel().IsSuccess.Should().BeTrue();
        o.Status.Should().Be(ProductionOrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_from_Completed_fails()
    {
        var o = Issued();
        o.Start(DateTime.UtcNow);
        o.Complete(100m, DateTime.UtcNow);
        o.Cancel().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_already_Cancelled_fails()
    {
        var o = Issued();
        o.Cancel();
        o.Cancel().IsFailure.Should().BeTrue();
    }
}
