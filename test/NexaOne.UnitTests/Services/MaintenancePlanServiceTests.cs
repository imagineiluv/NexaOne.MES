using Moq;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.Common;
using NexaOne.ServiceContracts.Mdm;

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
        new(planRepo.Object, partRepo.Object, new TestEquipmentDirectory());

    private static SparePartAdjustmentContext Adjustment(string key, string? transactionType = null) => new(
        MaintenanceCommandContext.Create(
            "maintenance-login", key, "MES", "PANEL-01", "corr-parts").Value,
        transactionType,
        WorkOrderId: "WO001",
        EquipmentId: "EQ001",
        Remark: "bearing replacement");

    private static MaintenanceCommandContext PlanCommand(string key, string actor = "maintenance-login") =>
        MaintenanceCommandContext.Create(actor, key, "POP", "PANEL-01", "corr-plan").Value;

    // ── CreatePlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlan_valid_data_succeeds()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.AddWithActionAsync(
            It.IsAny<MaintenancePlan>(), It.IsAny<MaintenancePlanAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo)
            .CreatePlanAsync("MP001", "월간 PM", "EQ001", "PM", "Monthly", Scheduled, 4m,
                "tech01", PlanCommand("plan-create"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(MaintenancePlanStatus.Planned);
        planRepo.Verify(r => r.AddWithActionAsync(
            It.IsAny<MaintenancePlan>(),
            It.Is<MaintenancePlanAction>(x => x.ActorId == "maintenance-login"
                && x.IdempotencyKey == "plan-create"
                && x.ClientChannel == "POP"
                && x.DeviceId == "PANEL-01"
                && x.CorrelationId == "corr-plan"
                && x.ActionType == "Create"
                && x.FromStatus == null
                && x.ToStatus == "Planned"), default), Times.Once);
    }

    [Theory]
    [InlineData("BM", "BM")]
    [InlineData("CM", "BM")]
    public void MaintenancePlan_uses_BM_as_the_canonical_breakdown_term(
        string input,
        string expected)
    {
        var result = MaintenancePlan.Create(
            "MP-BM", "고장 보전", "EQ001", input, "Monthly", Scheduled, 2m, "tech01");

        result.IsSuccess.Should().BeTrue();
        result.Value.PlanType.Should().Be(expected);
    }

    [Fact]
    public async Task CreatePlan_invalid_plan_type_fails()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();

        var result = await BuildService(planRepo, partRepo)
            .CreatePlanAsync("MP001", "월간 PM", "EQ001", "INVALID", "Monthly", Scheduled, 4m,
                "tech01", PlanCommand("plan-invalid"));

        result.IsFailure.Should().BeTrue();
        planRepo.Verify(r => r.AddWithActionAsync(
            It.IsAny<MaintenancePlan>(), It.IsAny<MaintenancePlanAction>(), default), Times.Never);
    }

    [Fact]
    public async Task CreatePlan_exact_replay_succeeds_but_changed_payload_conflicts()
    {
        var existing = PlannedPlan();
        var action = new MaintenancePlanAction(
            "A1", "MP001", "Create", null, "Planned", "maintenance-login",
            "plan-create-replay", DateTime.UtcNow, "Manual", "POP", "PANEL-01", "corr-plan");
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetActionByIdempotencyKeyAsync("plan-create-replay", default))
            .ReturnsAsync(action);
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(existing);
        var service = BuildService(planRepo, partRepo);

        var replay = await service.CreatePlanAsync(
            "MP001", "월간 PM", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01",
            PlanCommand("plan-create-replay"));
        var conflict = await service.CreatePlanAsync(
            "MP001", "변경된 계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01",
            PlanCommand("plan-create-replay"));

        replay.IsSuccess.Should().BeTrue();
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EMS.MaintenancePlan.IdempotencyConflict");
        planRepo.Verify(r => r.AddWithActionAsync(
            It.IsAny<MaintenancePlan>(), It.IsAny<MaintenancePlanAction>(), default), Times.Never);
    }

    [Fact]
    public async Task StartPlan_lost_status_guard_returns_concurrent_write()
    {
        var plan = PlannedPlan();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);
        planRepo.Setup(r => r.UpdateWithActionAsync(
            plan, It.IsAny<MaintenancePlanAction>(), default)).ReturnsAsync(false);

        var result = await BuildService(planRepo, partRepo)
            .StartPlanAsync("MP001", PlanCommand("plan-start-race"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.MaintenancePlan.ConcurrentWrite");
    }

    [Fact]
    public async Task StartPlan_exact_replay_does_not_load_or_write_the_plan()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetActionByIdempotencyKeyAsync("plan-start-replay", default))
            .ReturnsAsync(new MaintenancePlanAction(
                "A1", "MP001", "Start", "Planned", "InProgress", "maintenance-login",
                "plan-start-replay", DateTime.UtcNow, "Manual", "POP", "PANEL-01", "corr-plan"));

        var result = await BuildService(planRepo, partRepo)
            .StartPlanAsync("MP001", PlanCommand("plan-start-replay"));

        result.IsSuccess.Should().BeTrue();
        planRepo.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never);
        planRepo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<MaintenancePlan>(), It.IsAny<MaintenancePlanAction>(), default), Times.Never);
    }

    [Fact]
    public async Task StartPlan_rejects_a_work_order_action_that_reused_the_same_global_key()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetActionByIdempotencyKeyAsync("shared-start-key", default))
            .ReturnsAsync(new MaintenancePlanAction(
                "A1", "MP001", "Start", "Issued", "InProgress", "maintenance-login",
                "shared-start-key", DateTime.UtcNow, "Manual", "POP", "PANEL-01", "corr-plan",
                WorkOrderId: "WO001"));

        var result = await BuildService(planRepo, partRepo)
            .StartPlanAsync("MP001", PlanCommand("shared-start-key"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMS.MaintenancePlan.IdempotencyConflict");
        planRepo.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    // ── StartPlanAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task StartPlan_transitions_to_InProgress()
    {
        var plan = PlannedPlan();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);
        planRepo.Setup(r => r.UpdateWithActionAsync(
            plan, It.IsAny<MaintenancePlanAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo)
            .StartPlanAsync("MP001", PlanCommand("plan-start"));

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.InProgress);
        planRepo.Verify(r => r.UpdateWithActionAsync(
            plan,
            It.Is<MaintenancePlanAction>(x => x.ActionType == "Start"
                && x.FromStatus == "Planned" && x.ToStatus == "InProgress"
                && x.ActorId == "maintenance-login"), default), Times.Once);
    }

    [Fact]
    public async Task StartPlan_not_found_returns_failure()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP999", default)).ReturnsAsync((MaintenancePlan?)null);

        var result = await BuildService(planRepo, partRepo)
            .StartPlanAsync("MP999", PlanCommand("plan-start-missing"));

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
        planRepo.Setup(r => r.UpdateWithActionAsync(
            plan, It.IsAny<MaintenancePlanAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo)
            .CompletePlanAsync("MP001", PlanCommand("plan-complete"));

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

        var result = await BuildService(planRepo, partRepo)
            .CompletePlanAsync("MP001", PlanCommand("plan-complete-invalid"));

        result.IsFailure.Should().BeTrue();
        planRepo.Verify(r => r.UpdateWithActionAsync(
            It.IsAny<MaintenancePlan>(), It.IsAny<MaintenancePlanAction>(), default), Times.Never);
    }

    // ── CancelPlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPlan_from_Planned_succeeds()
    {
        var plan = PlannedPlan();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        planRepo.Setup(r => r.GetByIdAsync("MP001", default)).ReturnsAsync(plan);
        planRepo.Setup(r => r.UpdateWithActionAsync(
            plan, It.IsAny<MaintenancePlanAction>(), default)).ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo)
            .CancelPlanAsync("MP001", PlanCommand("plan-cancel"));

        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Cancelled);
    }

    // ── CreatePartAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePart_valid_data_succeeds()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.TryAddWithOpeningBalanceAsync(
            It.IsAny<SparePart>(), It.IsAny<SparePartStockTransaction>(), default))
            .ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo)
            .CreatePartAsync("SP001", "오일 필터", "P-001", "엔진 오일 필터", "EA",
                10m, 3m, 50m, "A-01", null, PlanCommand("part-create"));

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStock.Should().Be(10m);
        partRepo.Verify(r => r.TryAddWithOpeningBalanceAsync(
            It.IsAny<SparePart>(),
            It.Is<SparePartStockTransaction>(x =>
                x.TransactionType == "Opening"
                && x.Quantity == 10m
                && x.BalanceBefore == 0m
                && x.BalanceAfter == 10m
                && x.ActorId == "maintenance-login"
                && x.IdempotencyKey == "part-create"),
            default), Times.Once);
    }

    // ── AdjustStockAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AdjustStock_increases_stock()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default)).ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", 5m, Adjustment("idem-parts-in"));

        result.IsSuccess.Should().BeTrue();
        part.CurrentStock.Should().Be(15m);
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.Is<SparePartStockTransaction>(x => x.ActorId == "maintenance-login"
                && x.IdempotencyKey == "idem-parts-in"
                && x.TransactionType == "Incoming"
                && x.BalanceBefore == 10m
                && x.BalanceAfter == 15m
                && x.Quantity == 5m), It.IsAny<string?>(), default), Times.Once);
    }

    [Fact]
    public async Task AdjustStock_usage_with_equipment_builds_authenticated_usage_ledger()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.IsUsageScopeValidAsync(
                "SP001", "EQ001", "CLASS01", "BOM001", "WO001", default))
            .ReturnsAsync(true);
        partRepo.Setup(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default)).ReturnsAsync(true);
        var context = Adjustment("idem-parts-usage", "Usage") with { BomItemId = "BOM001" };

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -2m, context);

        result.IsSuccess.Should().BeTrue();
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.Is<SparePartStockTransaction>(x => x.Usage != null
                && x.Usage.InoutId == x.InoutId
                && x.Usage.PartId == "SP001"
                && x.Usage.BomItemId == "BOM001"
                && x.Usage.EquipmentId == "EQ001"
                && x.Usage.WorkOrderId == "WO001"
                && x.Usage.Quantity == 2m
                && x.Usage.UsedBy == "maintenance-login"), "CLASS01", default), Times.Once);
    }

    [Fact]
    public async Task AdjustStock_usage_with_invalid_equipment_bom_or_work_order_is_rejected()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.IsUsageScopeValidAsync(
                "SP001", "EQ001", "CLASS01", "BOM-WRONG", "WO001", default))
            .ReturnsAsync(false);
        var context = Adjustment("idem-parts-invalid-scope", "Usage") with
        {
            BomItemId = "BOM-WRONG"
        };

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -2m, context);

        result.IsFailure.Should().BeTrue();
        part.CurrentStock.Should().Be(10m, "a rejected usage must not mutate the loaded aggregate");
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Usage")]
    public async Task AdjustStock_usage_without_equipment_is_rejected_before_loading_stock(
        string? transactionType)
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        var context = Adjustment("idem-parts-no-equipment-usage", transactionType) with
        {
            EquipmentId = null
        };

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -2m, context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(nameof(SparePartAdjustmentContext.EquipmentId));
        partRepo.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never);
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Theory]
    [InlineData("Scrap")]
    [InlineData("Adjustment")]
    public async Task AdjustStock_equipment_independent_decrease_requires_explicit_non_usage_type(
        string transactionType)
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default)).ReturnsAsync(true);
        var context = Adjustment($"idem-parts-{transactionType}", transactionType) with
        {
            EquipmentId = null,
            WorkOrderId = null
        };

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -2m, context);

        result.IsSuccess.Should().BeTrue();
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.Is<SparePartStockTransaction>(x => x.Usage == null
                && x.EquipmentId == null
                && x.TransactionType == transactionType
                && x.BalanceAfter == 8m), null, default), Times.Once);
    }

    [Fact]
    public async Task AdjustStock_non_usage_with_bom_item_is_rejected_before_persistence()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        var context = Adjustment("idem-parts-incoming-bom", "Incoming") with
        {
            BomItemId = "BOM001"
        };

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", 2m, context);

        result.IsFailure.Should().BeTrue();
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task AdjustStock_part_not_found_fails()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP999", default)).ReturnsAsync((SparePart?)null);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP999", 5m, Adjustment("idem-parts-missing"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AdjustStock_below_zero_fails()
    {
        var part = TestPart();
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.IsUsageScopeValidAsync(
                "SP001", "EQ001", "CLASS01", null, "WO001", default))
            .ReturnsAsync(true);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -100m, Adjustment("idem-parts-short", "Usage"));

        result.IsFailure.Should().BeTrue();
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task AdjustStock_same_idempotency_key_returns_success_without_second_write()
    {
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.Setup(r => r.GetTransactionByIdempotencyKeyAsync("idem-parts-replay", default))
            .ReturnsAsync(new SparePartStockTransaction(
                "TX1", "SP001", "Usage", 2m, 10m, 8m,
                "maintenance-login", DateTime.UtcNow, "idem-parts-replay", "MES",
                "PANEL-01", "corr-parts", "WO001", "EQ001", "A-01", null,
                "bearing replacement",
                new SparePartUsage(
                    "US1", "TX1", "SP001", null, "EQ001", "WO001", 2m,
                    "maintenance-login", DateTime.UtcNow, "bearing replacement")));

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -2m, Adjustment("idem-parts-replay", "Usage"));

        result.IsSuccess.Should().BeTrue();
        partRepo.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never);
        partRepo.Verify(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task AdjustStock_guard_race_replays_the_same_idempotency_winner()
    {
        var part = TestPart();
        var winner = new SparePartStockTransaction(
            "TX-WINNER", "SP001", "Usage", 2m, 10m, 8m,
            "maintenance-login", DateTime.UtcNow, "idem-parts-race", "MES",
            "PANEL-01", "corr-parts", "WO001", "EQ001", "A-01", null,
            "bearing replacement",
            new SparePartUsage(
                "US-WINNER", "TX-WINNER", "SP001", null, "EQ001", "WO001", 2m,
                "maintenance-login", DateTime.UtcNow, "bearing replacement"));
        var planRepo = new Mock<IMaintenancePlanRepository>();
        var partRepo = new Mock<ISparePartRepository>();
        partRepo.SetupSequence(r => r.GetTransactionByIdempotencyKeyAsync("idem-parts-race", default))
            .ReturnsAsync((SparePartStockTransaction?)null)
            .ReturnsAsync(winner);
        partRepo.Setup(r => r.GetByIdAsync("SP001", default)).ReturnsAsync(part);
        partRepo.Setup(r => r.IsUsageScopeValidAsync(
                "SP001", "EQ001", "CLASS01", null, "WO001", default))
            .ReturnsAsync(true);
        partRepo.Setup(r => r.PersistAdjustmentAsync(
            It.IsAny<SparePartStockTransaction>(), It.IsAny<string?>(), default)).ReturnsAsync(false);

        var result = await BuildService(planRepo, partRepo).AdjustStockAsync(
            "SP001", -2m, Adjustment("idem-parts-race", "Usage"));

        result.IsSuccess.Should().BeTrue();
        partRepo.Verify(r => r.GetTransactionByIdempotencyKeyAsync("idem-parts-race", default),
            Times.Exactly(2));
    }

    private sealed class TestEquipmentDirectory : IEquipmentDirectory
    {
        public Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(
            string plantId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["EQ001"]);

        public Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
            string equipmentId,
            CancellationToken ct = default)
            => Task.FromResult<EquipmentDirectoryEntry?>(
                string.Equals(equipmentId, "EQ001", StringComparison.OrdinalIgnoreCase)
                    ? new EquipmentDirectoryEntry(equipmentId, "PLANT01", "CLASS01", true)
                    : null);

        public Task<bool> EquipmentClassExistsAsync(
            string equipmentClassId,
            CancellationToken ct = default)
            => Task.FromResult(string.Equals(
                equipmentClassId, "CLASS01", StringComparison.OrdinalIgnoreCase));
    }
}
