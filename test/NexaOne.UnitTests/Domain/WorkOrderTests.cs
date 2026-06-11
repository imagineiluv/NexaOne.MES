using NexaOne.EMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class WorkOrderTests
{
    private static readonly DateTime Issued = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static WorkOrder IssuedWo() =>
        WorkOrder.Create("WO001", "EQ001", "PM", "Scheduled maintenance", "tech01", Issued).Value;

    [Fact]
    public void Create_work_order_starts_in_Issued()
    {
        var wo = IssuedWo();
        wo.Status.Should().Be(WorkOrderStatus.Issued);
        wo.StartedAt.Should().BeNull();
    }

    [Fact]
    public void Create_with_invalid_type_fails()
    {
        var result = WorkOrder.Create("WO002", "EQ001", "INVALID", "desc", "tech01", Issued);
        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("PM");
    }

    [Fact]
    public void Create_with_empty_equipment_fails()
    {
        var result = WorkOrder.Create("WO003", "", "PM", "desc", "tech01", Issued);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_moves_to_InProgress()
    {
        var wo = IssuedWo();
        wo.Start().IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.InProgress);
        wo.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public void Start_from_non_Issued_fails()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Start().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Complete_moves_to_Completed()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Complete("done").IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Completed);
        wo.CompletedAt.Should().NotBeNull();
        wo.Remark.Should().Be("done");
    }

    [Fact]
    public void Complete_from_non_InProgress_fails()
    {
        var wo = IssuedWo();
        wo.Complete().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Issued_succeeds()
    {
        var wo = IssuedWo();
        wo.Cancel().IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_from_Completed_fails()
    {
        var wo = IssuedWo();
        wo.Start();
        wo.Complete();
        wo.Cancel().IsFailure.Should().BeTrue();
    }
}
