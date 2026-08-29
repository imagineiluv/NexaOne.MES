using Moq;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class EmsServiceTests
{
    private static readonly DateTime Issued = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static WorkOrder IssuedWo(string id = "WO001") =>
        WorkOrder.Create(id, "EQ001", "PM", "Scheduled maintenance", "tech01", Issued).Value;

    private EmsService BuildService(
        Mock<IWorkOrderRepository> repo,
        Mock<IMaintenancePlanRepository>? plans = null)
    {
        if (plans is null)
        {
            plans = new Mock<IMaintenancePlanRepository>();
            plans.Setup(r => r.GetByIdAsync(It.IsAny<string>(), default))
                .ReturnsAsync((string id, CancellationToken _) => MaintenancePlan.Create(
                    id, id, "EQ001", "PM", "Monthly", DateTime.UtcNow, 1m, "tech01").Value);
        }
        return new EmsService(repo.Object, plans.Object);
    }

    private static MaintenanceCommandContext Command(string key) =>
        MaintenanceCommandContext.Create("login-tech", key, "MES", correlationId: "corr-ems").Value;

    private static MaintenanceAction Winner(
        string actionType,
        string key,
        string? fromStatus,
        string toStatus,
        string? remark = null,
        string actorId = "login-tech",
        string source = "Manual") => new(
        $"A-{actionType}", "WO001", actionType, fromStatus, toStatus, actorId,
        key, DateTime.UtcNow, Source: source, CorrelationId: "corr-ems", Remark: remark);

    private static WorkOrder WorkOrderFor(string actionType)
    {
        var workOrder = IssuedWo();
        if (actionType == "Complete") workOrder.Start();
        return workOrder;
    }

    private static Task<Result> ExecuteTransitionAsync(
        EmsService service,
        string actionType,
        MaintenanceCommandContext command) => actionType switch
        {
            "Start" => service.StartWorkOrderAsync("WO001", command),
            "Complete" => service.CompleteWorkOrderAsync("WO001", "All done", command),
            "Cancel" => service.CancelWorkOrderAsync("WO001", command),
            _ => throw new ArgumentOutOfRangeException(nameof(actionType)),
        };

    // ── CreateWorkOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateWorkOrder_valid_data_succeeds()
    {
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.AddWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
            It.IsAny<WorkOrderCreateCommandRecord>(), default)).ReturnsAsync(true);

        var result = await BuildService(repo).CreateWorkOrderAsync(
            "WO001", "EQ001", "PM", "Maintenance", "tech01", "MP001", Command("idem-create"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(WorkOrderStatus.Issued);
        repo.Verify(r => r.AddWithActionAsync(
            It.Is<WorkOrder>(w => w.PlanId == "MP001"),
            It.Is<MaintenanceAction>(a => a.ActorId == "login-tech"
                && a.IdempotencyKey == "idem-create"
                && a.FromStatus == null
                && a.ToStatus == "Issued"),
            It.Is<WorkOrderCreateCommandRecord>(c => c.ActorId == "login-tech"
                && c.IdempotencyKey == "idem-create"), default), Times.Once);
    }

    [Fact]
    public async Task CreateWorkOrder_replays_persistent_creation_payload_and_actor()
    {
        const string key = "idem-create-ledger";
        WorkOrderCreateCommandRecord? ledger = null;
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetCreateCommandAsync(key, default))
            .ReturnsAsync(() => ledger);
        repo.Setup(r => r.AddWithActionAsync(
                It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
                It.IsAny<WorkOrderCreateCommandRecord>(), default))
            .Callback<WorkOrder, MaintenanceAction, WorkOrderCreateCommandRecord, CancellationToken>(
                (_, _, command, _) => ledger = command)
            .ReturnsAsync(true);
        var service = BuildService(repo);

        var first = await service.CreateWorkOrderAsync(
            "WO001", "EQ001", "PM", "Maintenance", "tech01", "MP001", Command(key));
        var replay = await service.CreateWorkOrderAsync(
            "WO001", "EQ001", "PM", "Maintenance", "tech01", "MP001", Command(key));

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Status.Should().Be(WorkOrderStatus.Issued);
        replay.Value.IssuedAt.Should().Be(first.Value.IssuedAt);
        ledger!.ActorId.Should().Be("login-tech");
        repo.Verify(r => r.AddWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
            It.IsAny<WorkOrderCreateCommandRecord>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateWorkOrder_invalid_type_fails()
    {
        var repo = new Mock<IWorkOrderRepository>();
        var result = await BuildService(repo).CreateWorkOrderAsync(
            "WO001", "EQ001", "INVALID", "desc", "tech01", null, Command("idem-invalid"));
        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
            It.IsAny<WorkOrderCreateCommandRecord>(), default), Times.Never);
    }

    [Theory]
    [InlineData("EQ002", "PM", "EMS.WorkOrder.PlanEquipmentMismatch")]
    [InlineData("EQ001", "BM", "EMS.WorkOrder.PlanTypeMismatch")]
    public async Task CreateWorkOrder_rejects_a_plan_with_incompatible_scope_or_type(
        string planEquipmentId,
        string planType,
        string expectedCode)
    {
        var repo = new Mock<IWorkOrderRepository>();
        var plans = new Mock<IMaintenancePlanRepository>();
        plans.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(
            MaintenancePlan.Create(
                "MP001", "Plan", planEquipmentId, planType, "Monthly",
                DateTime.UtcNow, 1m, "tech01").Value);

        var result = await BuildService(repo, plans).CreateWorkOrderAsync(
            "WO001", "EQ001", "PM", "Maintenance", "tech01", "MP001",
            Command($"idem-{expectedCode}"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
        repo.Verify(r => r.AddWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
            It.IsAny<WorkOrderCreateCommandRecord>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateWorkOrder_guard_race_replays_only_when_full_create_payload_matches()
    {
        const string key = "idem-create-race";
        var existing = WorkOrder.Create(
            "WO001", "EQ001", "PM", "Maintenance", "tech01", Issued, "MP001").Value;
        var repo = new Mock<IWorkOrderRepository>();
        repo.SetupSequence(r => r.GetActionByIdempotencyKeyAsync(key, default))
            .ReturnsAsync((MaintenanceAction?)null)
            .ReturnsAsync(Winner("Create", key, null, "Issued"));
        repo.Setup(r => r.AddWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
            It.IsAny<WorkOrderCreateCommandRecord>(), default)).ReturnsAsync(false);
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(existing);

        var result = await BuildService(repo).CreateWorkOrderAsync(
            "WO001", "EQ001", "PM", "Maintenance", "tech01", "MP001", Command(key));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(existing);
        repo.Verify(r => r.GetActionByIdempotencyKeyAsync(key, default), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateWorkOrder_guard_race_rejects_same_key_with_different_create_payload()
    {
        const string key = "idem-create-conflict";
        var winner = WorkOrder.Create(
            "WO001", "EQ001", "PM", "Winner description", "tech01", Issued, "MP001").Value;
        var repo = new Mock<IWorkOrderRepository>();
        repo.SetupSequence(r => r.GetActionByIdempotencyKeyAsync(key, default))
            .ReturnsAsync((MaintenanceAction?)null)
            .ReturnsAsync(Winner("Create", key, null, "Issued"));
        repo.Setup(r => r.AddWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(),
            It.IsAny<WorkOrderCreateCommandRecord>(), default)).ReturnsAsync(false);
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(winner);

        var result = await BuildService(repo).CreateWorkOrderAsync(
            "WO001", "EQ001", "PM", "Different description", "tech01", "MP001", Command(key));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.WorkOrder.IdempotencyConflict");
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    // ── StartWorkOrderAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task StartWorkOrder_issued_wo_succeeds()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(repo).StartWorkOrderAsync("WO001", Command("idem-start"));

        result.IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.InProgress);
        repo.Verify(r => r.UpdateWithActionAsync(
            wo, It.Is<MaintenanceAction>(a => a.FromStatus == "Issued"
                && a.ToStatus == "InProgress" && a.ActorId == "login-tech"), default), Times.Once);
    }

    [Fact]
    public async Task StartWorkOrder_not_found_returns_failure()
    {
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO999", default)).ReturnsAsync((WorkOrder?)null);

        var result = await BuildService(repo).StartWorkOrderAsync("WO999", Command("idem-start-missing"));

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
        repo.Setup(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(repo).CompleteWorkOrderAsync(
            "WO001", "All done", Command("idem-complete"));

        result.IsSuccess.Should().BeTrue();
        wo.Status.Should().Be(WorkOrderStatus.Completed);
    }

    [Fact]
    public async Task CompleteWorkOrder_issued_wo_fails()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);

        var result = await BuildService(repo).CompleteWorkOrderAsync(
            "WO001", "", Command("idem-complete-invalid"));

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default), Times.Never);
    }

    [Fact]
    public async Task CompleteWorkOrder_rejects_an_open_maintenance_labor_session()
    {
        var wo = IssuedWo();
        wo.Start();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.HasOpenLaborAsync("WO001", default)).ReturnsAsync(true);

        var result = await BuildService(repo).CompleteWorkOrderAsync(
            "WO001", "done", Command("idem-complete-open-labor"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.WorkOrder.OpenLabor");
        wo.Status.Should().Be(WorkOrderStatus.InProgress);
        repo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default), Times.Never);
    }

    // ── GetCountByStatusAsync ─────────────────────────────────────────────────
    // 대시보드 집계가 전체 목록 대신 COUNT(*)를 쓰도록 서비스가 리포지토리 카운트로 위임하는지 검증.

    [Fact]
    public async Task GetCountByStatus_delegates_to_repository_and_returns_count()
    {
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetCountByStatusAsync(WorkOrderStatus.Issued, default)).ReturnsAsync(7);

        var count = await BuildService(repo).GetCountByStatusAsync(WorkOrderStatus.Issued);

        count.Should().Be(7);
        repo.Verify(r => r.GetCountByStatusAsync(WorkOrderStatus.Issued, default), Times.Once);
        // 목록 조회 경로(GetByStatusAsync)는 더 이상 호출되지 않아야 한다(목록 적재 회피).
        repo.Verify(r => r.GetByStatusAsync(It.IsAny<WorkOrderStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CancelWorkOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CancelWorkOrder_issued_wo_succeeds()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(repo).CancelWorkOrderAsync("WO001", Command("idem-cancel"));

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

        var result = await BuildService(repo).CancelWorkOrderAsync(
            "WO001", Command("idem-cancel-completed"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CancelWorkOrder_rejects_an_open_maintenance_labor_session()
    {
        var wo = IssuedWo();
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(wo);
        repo.Setup(r => r.HasOpenLaborAsync("WO001", default)).ReturnsAsync(true);

        var result = await BuildService(repo).CancelWorkOrderAsync(
            "WO001", Command("idem-cancel-open-labor"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.WorkOrder.OpenLabor");
        wo.Status.Should().Be(WorkOrderStatus.Issued);
        repo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default), Times.Never);
    }

    [Fact]
    public async Task StartWorkOrder_same_idempotency_key_replays_without_second_update()
    {
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetActionByIdempotencyKeyAsync("idem-replay", default))
            .ReturnsAsync(new MaintenanceAction(
                "A1", "WO001", "Start", "Issued", "InProgress", "login-tech",
                "idem-replay", DateTime.UtcNow, CorrelationId: "corr-ems"));

        var result = await BuildService(repo).StartWorkOrderAsync("WO001", Command("idem-replay"));

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never);
        repo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default), Times.Never);
    }

    [Theory]
    [InlineData("Start", "Issued", "InProgress", null)]
    [InlineData("Complete", "InProgress", "Completed", "All done")]
    [InlineData("Cancel", "Issued", "Cancelled", null)]
    public async Task WorkOrder_transition_guard_race_replays_same_payload(
        string actionType,
        string fromStatus,
        string toStatus,
        string? remark)
    {
        var key = $"idem-{actionType.ToLowerInvariant()}-race";
        var repo = new Mock<IWorkOrderRepository>();
        repo.SetupSequence(r => r.GetActionByIdempotencyKeyAsync(key, default))
            .ReturnsAsync((MaintenanceAction?)null)
            .ReturnsAsync(Winner(actionType, key, fromStatus, toStatus, remark));
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(WorkOrderFor(actionType));
        repo.Setup(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default)).ReturnsAsync(false);

        var result = await ExecuteTransitionAsync(BuildService(repo), actionType, Command(key));

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.GetActionByIdempotencyKeyAsync(key, default), Times.Exactly(2));
        repo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default), Times.Once);
    }

    [Theory]
    [InlineData("Start", "Issued", "InProgress", null)]
    [InlineData("Complete", "InProgress", "Completed", "All done")]
    [InlineData("Cancel", "Issued", "Cancelled", null)]
    public async Task WorkOrder_transition_guard_race_rejects_same_key_with_different_payload(
        string actionType,
        string fromStatus,
        string toStatus,
        string? remark)
    {
        var key = $"idem-{actionType.ToLowerInvariant()}-conflict";
        var repo = new Mock<IWorkOrderRepository>();
        repo.SetupSequence(r => r.GetActionByIdempotencyKeyAsync(key, default))
            .ReturnsAsync((MaintenanceAction?)null)
            .ReturnsAsync(Winner(actionType, key, fromStatus, toStatus, remark, source: "Scheduler"));
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(WorkOrderFor(actionType));
        repo.Setup(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default)).ReturnsAsync(false);

        var result = await ExecuteTransitionAsync(BuildService(repo), actionType, Command(key));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.WorkOrder.IdempotencyConflict");
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task StartWorkOrder_guard_race_without_idempotency_winner_is_concurrent_write_conflict()
    {
        const string key = "idem-start-no-winner";
        var repo = new Mock<IWorkOrderRepository>();
        repo.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(IssuedWo());
        repo.Setup(r => r.UpdateWithActionAsync(
            It.IsAny<WorkOrder>(), It.IsAny<MaintenanceAction>(), default)).ReturnsAsync(false);

        var result = await BuildService(repo).StartWorkOrderAsync("WO001", Command(key));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.WorkOrder.ConcurrentWrite");
    }
}
