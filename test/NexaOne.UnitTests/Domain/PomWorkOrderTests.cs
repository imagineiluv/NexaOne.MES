using NexaOne.POM.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class PomWorkOrderTests
{
    private static readonly DateTime Start = new(2026, 7, 11, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(8);

    private static PomWorkOrder Created(decimal planQty = 100m) =>
        PomWorkOrder.Create(
            "WO001", "PO001", "P1", "Cutting", "PROD01", planQty,
            Start, End, "CUT", "EQ001", "operator", "planner",
            "RT01", 10, "WC01").Value;

    [Fact]
    public void Create_requires_production_order_parent()
        => PomWorkOrder.Create(
                "WO001", " ", "P1", "Cutting", "PROD01", 100m,
                Start, End, "CUT", "EQ001", null, "planner")
            .IsFailure.Should().BeTrue();

    [Fact]
    public void Create_preserves_work_instruction_boundary_fields()
    {
        var workOrder = Created();

        workOrder.ProductionOrderId.Should().Be("PO001");
        workOrder.ProcessId.Should().Be("CUT");
        workOrder.EquipmentId.Should().Be("EQ001");
        workOrder.RoutingId.Should().Be("RT01");
        workOrder.RoutingScope.Should().Be(PomWorkOrderRoutingScope.Operation);
        workOrder.RoutingStepNo.Should().Be(10);
        workOrder.Status.Should().Be(PomWorkOrderStatus.Created);
    }

    [Fact]
    public void Create_resolves_and_validates_routing_scope()
    {
        var serial = PomWorkOrder.Create(
            "WO-SERIAL", "PO001", "P1", "Full route", "PROD01", 100m,
            Start, End, null, null, null, "planner", routingId: "RT01");

        serial.IsSuccess.Should().BeTrue();
        serial.Value.RoutingScope.Should().Be(PomWorkOrderRoutingScope.SerialRoute);
        serial.Value.IsRoutingBound.Should().BeTrue();
        serial.Value.IsSerialRouting.Should().BeTrue();
        serial.Value.RoutingStepNo.Should().BeNull();
        PomWorkOrder.Create(
                "WO-STEP-ONLY", "PO001", "P1", "Cutting", "PROD01", 100m,
                Start, End, "CUT", "EQ001", null, "planner", routingStepNo: 10)
            .IsFailure.Should().BeTrue();
        var unbound = PomWorkOrder.Create(
                "WO-UNBOUND", "PO001", "P1", "Cutting", "PROD01", 100m,
                Start, End, "CUT", "EQ001", null, "planner");
        unbound.IsSuccess.Should().BeTrue();
        unbound.Value.RoutingScope.Should().Be(PomWorkOrderRoutingScope.Unbound);
        PomWorkOrder.Create(
                "WO-BAD-SERIAL", "PO001", "P1", "Full route", "PROD01", 100m,
                Start, End, "CUT", null, null, "planner", routingId: "RT01",
                routingScope: PomWorkOrderRoutingScope.SerialRoute)
            .IsFailure.Should().BeTrue("a serial-route work order cannot bind one process");
    }

    [Fact]
    public void Lifecycle_releases_starts_reports_and_completes()
    {
        var workOrder = Created();

        workOrder.Release("planner").IsSuccess.Should().BeTrue();
        workOrder.Start(Start, "operator").IsSuccess.Should().BeTrue();
        workOrder.ReportProduction(70m, 5m, "operator").IsSuccess.Should().BeTrue();
        workOrder.Complete(90m, 5m, End, "operator").IsSuccess.Should().BeTrue();

        workOrder.Status.Should().Be(PomWorkOrderStatus.Completed);
        workOrder.StartQty.Should().Be(100m);
        workOrder.CompleteQty.Should().Be(90m);
        workOrder.ScrapQty.Should().Be(5m);
        workOrder.CompletedAt.Should().Be(End);
    }

    [Fact]
    public void Hold_blocks_start_and_reporting()
    {
        var workOrder = Created();
        workOrder.Release("planner");
        workOrder.Hold("supervisor");

        workOrder.Start(Start, "operator").IsFailure.Should().BeTrue();
        workOrder.ReleaseHold("supervisor").IsSuccess.Should().BeTrue();
        workOrder.Start(Start, "operator").IsSuccess.Should().BeTrue();
        workOrder.Hold("supervisor");
        workOrder.ReportProduction(1m, 0m, "operator").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Report_rejects_quantity_over_started_quantity()
    {
        var workOrder = Created();
        workOrder.Release("planner");
        workOrder.Start(Start, "operator");

        workOrder.ReportProduction(99m, 2m, "operator").IsFailure.Should().BeTrue();
        workOrder.CompleteQty.Should().Be(0m);
        workOrder.ScrapQty.Should().Be(0m);
    }

    [Fact]
    public void Failed_complete_does_not_erase_previous_report()
    {
        var workOrder = Created();
        workOrder.Release("planner");
        workOrder.Start(Start, "operator");
        workOrder.ReportProduction(40m, 2m, "operator");

        workOrder.Complete(0m, 0m, End, "operator").IsFailure.Should().BeTrue();

        workOrder.Status.Should().Be(PomWorkOrderStatus.Started);
        workOrder.CompleteQty.Should().Be(40m);
        workOrder.ScrapQty.Should().Be(2m);
        workOrder.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Cancel_is_only_allowed_before_execution()
    {
        var created = Created();
        created.Cancel("planner").IsSuccess.Should().BeTrue();
        created.Status.Should().Be(PomWorkOrderStatus.Cancelled);

        var started = Created();
        started.Release("planner");
        started.Start(Start, "operator");
        started.Cancel("planner").IsFailure.Should().BeTrue();
    }
}
