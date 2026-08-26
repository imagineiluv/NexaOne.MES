using Moq;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.UnitTests.Services;

public sealed class PomWorkOrderServiceTests
{
    private static readonly DateTime Start = new(2026, 7, 11, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(8);
    private readonly Mock<IPomWorkOrderRepository> _workOrders = new();
    private readonly Mock<IProductionOrderRepository> _productionOrders = new();
    private readonly Mock<ILotRepository> _lots = new();
    private readonly Mock<IProductionQualityGateway> _productionQuality = new();

    public PomWorkOrderServiceTests()
    {
        _lots.Setup(r => r.GetByWorkOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private PomWorkOrderService Build() => new(
        _workOrders.Object, _productionOrders.Object, _lots.Object, _productionQuality.Object);

    private static ProductionOrder Parent(string productId = "PROD01") =>
        ProductionOrder.Create("PO001", "PLAN001", "EQ001", productId, 100m, Start, End).Value;

    private static PomWorkOrder ReleasedWorkOrder(string id = "WO001")
    {
        var workOrder = PomWorkOrder.Create(
            id, "PO001", "P1", "Cutting", "PROD01", 100m,
            Start, End, "CUT", "EQ001", "operator", "planner").Value;
        workOrder.Release("planner");
        return workOrder;
    }

    private static PomWorkOrder StartedWorkOrder(string id = "WO001")
    {
        var workOrder = ReleasedWorkOrder(id);
        workOrder.Start(Start, "operator");
        return workOrder;
    }

    private static PomWorkOrder BoundReleasedWorkOrder(string id, int stepNo)
    {
        var workOrder = PomWorkOrder.Create(
            id, "PO001", "P1", id, "PROD01", 100m,
            Start, End, $"OP{stepNo}", "EQ001", "operator", "planner",
            routingId: "RT1", routingStepNo: stepNo).Value;
        workOrder.Release("planner");
        return workOrder;
    }

    private static PomWorkOrder BoundStartedWorkOrder(string id = "WO020", int stepNo = 20)
    {
        var workOrder = BoundReleasedWorkOrder(id, stepNo);
        workOrder.Start(Start, "operator");
        return workOrder;
    }

    private static PomWorkOrder SerialReleasedWorkOrder(string id = "WO-SERIAL")
    {
        var workOrder = PomWorkOrder.Create(
            id, "PO001", "P1", id, "PROD01", 100m,
            Start, End, null, null, "operator", "planner",
            routingId: "RT1", routingScope: PomWorkOrderRoutingScope.SerialRoute).Value;
        workOrder.Release("planner");
        return workOrder;
    }

    private static Lot QueuedLot(string id = "LOT001", IReadOnlyList<string>? routeSteps = null) =>
        Lot.Create(id, "P1", "WO001", "PROD01", 10m, routeSteps ?? ["CUT"], "operator").Value;

    private static Lot CompletedLot(string id = "LOT001", decimal defectQty = 2m)
    {
        var lot = QueuedLot(id);
        lot.TrackIn("EQ001", null, null, "operator", Start);
        lot.TrackOut("EQ001", 10m, defectQty, null, "operator", Start.AddHours(1));
        return lot;
    }

    private static Lot ConsumedLot(string id = "LOT002")
    {
        var lot = QueuedLot(id);
        lot.Consume("operator");
        return lot;
    }

    private static PomWorkOrderCreateCommand CreateCommand(string productId = "PROD01") => new(
        "WO001", "PO001", "P1", "Cutting", productId, 100m,
        Start, End, "CUT", "EQ001", "operator", "planner");

    private static PomWorkOrderOperationContext Context(
        string key = "op-001", string channel = "MOBILE", int expectedVersion = 1) =>
        new("operator", channel, key, expectedVersion, "PDA-01", "test");

    [Fact]
    public async Task Create_rejects_missing_production_order()
    {
        _productionOrders.Setup(r => r.GetByIdAsync("PO001", default))
            .ReturnsAsync((ProductionOrder?)null);

        var result = await Build().CreateAsync(CreateCommand());

        result.IsFailure.Should().BeTrue();
        _workOrders.Verify(r => r.AddAsync(It.IsAny<PomWorkOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task Create_rejects_product_mismatch_with_parent()
    {
        _productionOrders.Setup(r => r.GetByIdAsync("PO001", default)).ReturnsAsync(Parent());

        var result = await Build().CreateAsync(CreateCommand("OTHER"));

        result.IsFailure.Should().BeTrue();
        _workOrders.Verify(r => r.AddAsync(It.IsAny<PomWorkOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task Create_persists_child_with_parent_id()
    {
        _productionOrders.Setup(r => r.GetByIdAsync("PO001", default)).ReturnsAsync(Parent());
        _workOrders.Setup(r => r.AddAsync(It.IsAny<PomWorkOrder>(), default)).Returns(Task.CompletedTask);

        var result = await Build().CreateAsync(CreateCommand());

        result.IsSuccess.Should().BeTrue();
        result.Value.ProductionOrderId.Should().Be("PO001");
        _workOrders.Verify(r => r.AddAsync(
            It.Is<PomWorkOrder>(w => w.ProductionOrderId == "PO001" && w.ProcessId == "CUT"),
            default), Times.Once);
    }

    [Fact]
    public async Task Start_writes_transition_and_execution_atomically()
    {
        var workOrder = ReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default)).ReturnsAsync(true);

        var result = await Build().StartAsync("WO001", Context());

        result.IsSuccess.Should().BeTrue();
        workOrder.Status.Should().Be(PomWorkOrderStatus.Started);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            workOrder,
            It.Is<PomWorkOrderExecution>(e =>
                e.IdempotencyKey == "op-001" &&
                e.Action == PomWorkOrderAction.Start &&
                e.FromStatus == PomWorkOrderStatus.Released &&
                e.ToStatus == PomWorkOrderStatus.Started &&
                e.ClientChannel == "MOBILE" &&
                e.DeviceId == "PDA-01" &&
                e.ExpectedVersion == 1 && e.ResultVersion == 2),
            default), Times.Once);
    }

    [Fact]
    public async Task Exact_idempotent_retry_returns_current_state_without_second_write()
    {
        var workOrder = ReleasedWorkOrder();
        workOrder.Start(Start, "operator");
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.GetExecutionByIdempotencyKeyAsync("op-001", default))
            .ReturnsAsync(new PomWorkOrderExecution(
                "EX1", "WO001", "op-001", PomWorkOrderAction.Start,
                PomWorkOrderStatus.Released, PomWorkOrderStatus.Started,
                null, null, "operator", "EQ001", "MOBILE", "PDA-01", Start, "test",
                ExpectedVersion: 1, ResultVersion: 2));

        var result = await Build().StartAsync("WO001", Context());

        result.IsSuccess.Should().BeTrue();
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Reusing_idempotency_key_for_different_action_fails()
    {
        var workOrder = ReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.GetExecutionByIdempotencyKeyAsync("op-001", default))
            .ReturnsAsync(new PomWorkOrderExecution(
                "EX1", "WO001", "op-001", PomWorkOrderAction.Report,
                PomWorkOrderStatus.Started, PomWorkOrderStatus.Started,
                10m, 0m, "operator", "EQ001", "MOBILE", "PDA-01", Start));

        var result = await Build().StartAsync("WO001", Context());

        result.IsFailure.Should().BeTrue();
        workOrder.Status.Should().Be(PomWorkOrderStatus.Released);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Theory]
    [InlineData("", "MOBILE")]
    [InlineData("op-001", "UNKNOWN")]
    public async Task Operation_requires_key_and_known_channel(string key, string channel)
    {
        var workOrder = ReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);

        var result = await Build().StartAsync("WO001", Context(key, channel));

        result.IsFailure.Should().BeTrue();
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Domain_transition_failure_does_not_write_execution()
    {
        var workOrder = ReleasedWorkOrder();
        workOrder.Start(Start, "operator");
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);

        var result = await Build().StartAsync("WO001", Context());

        result.IsFailure.Should().BeTrue();
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Stale_expected_version_returns_conflict_without_write()
    {
        var workOrder = ReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);

        var result = await Build().StartAsync("WO001", Context(expectedVersion: 2));

        result.IsFailure.Should().BeTrue();
        workOrder.Status.Should().Be(PomWorkOrderStatus.Released);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Compare_and_swap_loss_returns_conflict()
    {
        var workOrder = ReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        var result = await Build().StartAsync("WO001", Context());
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Direct_start_of_bound_work_order_is_blocked_before_predecessor_lookup()
    {
        var predecessor = BoundReleasedWorkOrder("WO010", 10);
        var target = BoundReleasedWorkOrder("WO020", 20);
        _workOrders.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);
        _workOrders.Setup(r => r.GetByProductionOrderAsync("PO001", default))
            .ReturnsAsync([predecessor, target]);

        var result = await Build().StartAsync(target.Id, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("ROUTE_BOUND_LOT_EXECUTION_REQUIRED");
        target.Status.Should().Be(PomWorkOrderStatus.Released);
        _workOrders.Verify(r => r.GetByProductionOrderAsync(
            It.IsAny<string>(), default), Times.Never);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Direct_start_of_serial_route_work_order_requires_lot_execution()
    {
        var workOrder = SerialReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync(workOrder.Id, default)).ReturnsAsync(workOrder);

        var result = await Build().StartAsync(workOrder.Id, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("ROUTE_BOUND_LOT_EXECUTION_REQUIRED");
        workOrder.Status.Should().Be(PomWorkOrderStatus.Released);
        _workOrders.Verify(r => r.GetByProductionOrderAsync(
            It.IsAny<string>(), default), Times.Never);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Reusing_idempotency_key_with_different_expected_version_fails()
    {
        var workOrder = ReleasedWorkOrder();
        workOrder.Start(Start, "operator");
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.GetExecutionByIdempotencyKeyAsync("op-001", default))
            .ReturnsAsync(new PomWorkOrderExecution(
                "EX1", "WO001", "op-001", PomWorkOrderAction.Start,
                PomWorkOrderStatus.Released, PomWorkOrderStatus.Started,
                null, null, "operator", "EQ001", "MOBILE", "PDA-01", Start, "test",
                ExpectedVersion: 1, ResultVersion: 2));

        var result = await Build().StartAsync("WO001", Context(expectedVersion: 2));

        result.IsFailure.Should().BeTrue("the optimistic-concurrency version is part of request identity");
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Theory]
    [InlineData("other-operator", "MOBILE", "PDA-01", "test")]
    [InlineData("operator", "POP", "PDA-01", "test")]
    [InlineData("operator", "MOBILE", "PDA-02", "test")]
    [InlineData("operator", "MOBILE", "PDA-01", "changed remark")]
    public async Task Reusing_idempotency_key_with_different_audit_identity_fails(
        string user, string channel, string deviceId, string remark)
    {
        var workOrder = ReleasedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.GetExecutionByIdempotencyKeyAsync("op-001", default))
            .ReturnsAsync(new PomWorkOrderExecution(
                "EX1", "WO001", "op-001", PomWorkOrderAction.Start,
                PomWorkOrderStatus.Released, PomWorkOrderStatus.Started,
                null, null, "operator", "EQ001", "MOBILE", "PDA-01", Start, "test",
                ExpectedVersion: 1, ResultVersion: 2));
        var context = Context() with
        {
            User = user,
            ClientChannel = channel,
            DeviceId = deviceId,
            Remark = remark
        };

        var result = await Build().StartAsync("WO001", context);

        result.IsFailure.Should().BeTrue();
        workOrder.Status.Should().Be(PomWorkOrderStatus.Released);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Fact]
    public async Task Shared_predecessor_guard_allows_completed_lower_routing_step_for_lot_auto_start()
    {
        var predecessor = BoundReleasedWorkOrder("WO010", 10);
        predecessor.Start(Start, "operator");
        predecessor.Complete(100m, 0m, Start.AddHours(1), "operator");
        var target = BoundReleasedWorkOrder("WO020", 20);
        _workOrders.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);
        _workOrders.Setup(r => r.GetByProductionOrderAsync("PO001", default))
            .ReturnsAsync([predecessor, target]);
        var result = await WorkOrderRoutingPredecessorGuard.ValidateAsync(
            _workOrders.Object, target);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        target.Status.Should().Be(PomWorkOrderStatus.Released);
    }

    [Theory]
    [InlineData("Report")]
    [InlineData("Complete")]
    public async Task Direct_reporting_and_completion_of_bound_work_order_are_blocked(string action)
    {
        var workOrder = BoundStartedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync(workOrder.Id, default)).ReturnsAsync(workOrder);

        var result = action == "Report"
            ? await Build().ReportAsync(workOrder.Id, 50m, 0m, Context())
            : await Build().CompleteAsync(workOrder.Id, 100m, 0m, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("ROUTE_BOUND_LOT_EXECUTION_REQUIRED");
        workOrder.Status.Should().Be(PomWorkOrderStatus.Started);
        _lots.Verify(r => r.GetByWorkOrderAsync(
            It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Complete_without_linked_lots_preserves_manual_completion()
    {
        var workOrder = StartedWorkOrder();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default)).ReturnsAsync(true);

        var result = await Build().CompleteAsync("WO001", 9m, 1m, Context());

        result.IsSuccess.Should().BeTrue();
        workOrder.Status.Should().Be(PomWorkOrderStatus.Completed);
        _productionQuality.Verify(g => g.EvaluateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Complete_blocks_when_any_linked_lot_is_held()
    {
        var workOrder = StartedWorkOrder();
        var held = QueuedLot();
        held.Hold("quality");
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default)).ReturnsAsync([held]);

        var result = await Build().CompleteAsync("WO001", 10m, 0m, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("LOT001").And.Contain("Hold");
        workOrder.Status.Should().Be(PomWorkOrderStatus.Started);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
        _productionQuality.Verify(g => g.EvaluateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Complete_blocks_linked_lot_that_has_not_reached_a_terminal_state()
    {
        var workOrder = StartedWorkOrder();
        var intermediate = QueuedLot(routeSteps: ["CUT", "PACK"]);
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default)).ReturnsAsync([intermediate]);

        var result = await Build().CompleteAsync("WO001", 10m, 0m, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("LOT001").And.Contain("Queued");
        _productionQuality.Verify(g => g.EvaluateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never,
            "future process quality must not be evaluated before the lot reaches its final step");
    }

    [Fact]
    public async Task Complete_requires_payload_to_match_completed_lot_totals()
    {
        var workOrder = StartedWorkOrder();
        var completed = CompletedLot(defectQty: 2m);
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default)).ReturnsAsync([completed]);

        var result = await Build().CompleteAsync("WO001", 9m, 1m, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("good: 8").And.Contain("defect: 2");
        _productionQuality.Verify(g => g.EvaluateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Theory]
    [InlineData(ProductionQualityStatus.Pending)]
    [InlineData(ProductionQualityStatus.Failed)]
    public async Task Complete_blocks_pending_or_failed_quality_for_completed_lot(
        ProductionQualityStatus status)
    {
        var workOrder = StartedWorkOrder();
        var completed = CompletedLot();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default)).ReturnsAsync([completed]);
        var quality = status == ProductionQualityStatus.Pending
            ? ProductionQualityGateResult.Pending(1, 0, "SPEC-CUT")
            : ProductionQualityGateResult.Failed(1, 0, "SPEC-CUT");
        _productionQuality.Setup(g => g.EvaluateAsync("LOT001", "CUT", "WO001", default))
            .ReturnsAsync(quality);

        var result = await Build().CompleteAsync("WO001", 8m, 2m, Context());

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain(status.ToString()).And.Contain("SPEC-CUT");
        workOrder.Status.Should().Be(PomWorkOrderStatus.Started);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }

    [Theory]
    [InlineData(ProductionQualityStatus.NotRequired)]
    [InlineData(ProductionQualityStatus.Passed)]
    public async Task Complete_accepts_allowed_quality_and_ignores_consumed_lot(
        ProductionQualityStatus status)
    {
        var workOrder = StartedWorkOrder();
        var completed = CompletedLot();
        var consumed = ConsumedLot();
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default)).ReturnsAsync([completed, consumed]);
        var quality = status == ProductionQualityStatus.NotRequired
            ? ProductionQualityGateResult.NotRequired()
            : ProductionQualityGateResult.Passed(1);
        _productionQuality.Setup(g => g.EvaluateAsync("LOT001", "CUT", "WO001", default))
            .ReturnsAsync(quality);
        _workOrders.Setup(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default)).ReturnsAsync(true);

        var result = await Build().CompleteAsync("WO001", 8m, 2m, Context());

        result.IsSuccess.Should().BeTrue();
        workOrder.Status.Should().Be(PomWorkOrderStatus.Completed);
        _productionQuality.Verify(g => g.EvaluateAsync("LOT001", "CUT", "WO001", default), Times.Once);
        _productionQuality.Verify(g => g.EvaluateAsync("LOT002", It.IsAny<string>(), "WO001", default), Times.Never,
            "Consumed is a Mixing terminal state, not a final TrackOut quality boundary");
    }

    [Fact]
    public async Task Exact_complete_retry_returns_prior_success_before_rechecking_quality()
    {
        var workOrder = StartedWorkOrder();
        workOrder.Complete(8m, 2m, Start.AddHours(2), "operator");
        _workOrders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(workOrder);
        _workOrders.Setup(r => r.GetExecutionByIdempotencyKeyAsync("op-001", default))
            .ReturnsAsync(new PomWorkOrderExecution(
                "EX-COMPLETE", "WO001", "op-001", PomWorkOrderAction.Complete,
                PomWorkOrderStatus.Started, PomWorkOrderStatus.Completed,
                8m, 2m, "operator", "EQ001", "MOBILE", "PDA-01", Start.AddHours(2), "test",
                ExpectedVersion: 1, ResultVersion: 2));

        var result = await Build().CompleteAsync("WO001", 8m, 2m, Context());

        result.IsSuccess.Should().BeTrue();
        _lots.Verify(r => r.GetByWorkOrderAsync(It.IsAny<string>(), default), Times.Never);
        _productionQuality.Verify(g => g.EvaluateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
        _workOrders.Verify(r => r.UpdateWithExecutionAsync(
            It.IsAny<PomWorkOrder>(), It.IsAny<PomWorkOrderExecution>(), default), Times.Never);
    }
}
