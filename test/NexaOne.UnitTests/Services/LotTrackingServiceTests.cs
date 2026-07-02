using Moq;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.UnitTests.Services;

/// <summary>
/// §19.4 — LotTrackingService: 검증 순서(Hold/상태가 설비 조회보다 먼저),
/// 교차 모듈 마스터 검증(ITrackingMasterGateway), 이력 기록, W/O 자동 시작·마감,
/// Mixing 전수 검증 후 변경(UnitOfWork 부재 적응)을 검증한다.
/// </summary>
public sealed class LotTrackingServiceTests
{
    private static readonly DateTime ServerTime = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SchedStart = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SchedEnd   = new(2026, 6, 1, 18, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ILotRepository> _lots = new();
    private readonly Mock<ILotHistoryRepository> _histories = new();
    private readonly Mock<ILotMixingRelationRepository> _mixings = new();
    private readonly Mock<IProductionOrderRepository> _orders = new();
    private readonly Mock<ITrackingMasterGateway> _master = new();

    public LotTrackingServiceTests()
    {
        // 기본 마스터: P1 소속 가용 설비 EQ001(클래스 CLS01), Recipe/불량 코드는 모두 유효
        _master.Setup(m => m.GetEquipmentAsync("EQ001", default))
            .ReturnsAsync(new TrackingEquipmentInfo("EQ001", "P1", "CLS01", true));
        _master.Setup(m => m.IsUsableRecipeAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), default))
            .ReturnsAsync(true);
        _master.Setup(m => m.IsValidDefectCodeAsync(It.IsAny<string>(), default))
            .ReturnsAsync(true);
        _lots.Setup(r => r.AddAsync(It.IsAny<Lot>(), default)).Returns(Task.CompletedTask);
        _lots.Setup(r => r.UpdateAsync(It.IsAny<Lot>(), default)).Returns(Task.CompletedTask);
        _histories.Setup(r => r.AddAsync(It.IsAny<LotHistory>(), default)).Returns(Task.CompletedTask);
        _mixings.Setup(r => r.AddAsync(It.IsAny<LotMixingRelation>(), default)).Returns(Task.CompletedTask);
    }

    private LotTrackingService Build() =>
        new(_lots.Object, _histories.Object, _mixings.Object, _orders.Object, _master.Object);

    private static Lot QueuedLot(
        string id = "LOT001", string? workOrderId = null, decimal qty = 10m, params string[] steps) =>
        Lot.Create(id, "P1", workOrderId,
            "PROD01", qty, steps.Length == 0 ? ["CUT", "ASSY"] : steps, "tester").Value;

    private static Lot ProcessingLot(
        string id = "LOT001", string? workOrderId = null, decimal qty = 10m, params string[] steps)
    {
        var lot = QueuedLot(id, workOrderId, qty, steps);
        lot.TrackIn("EQ001", "RCP01", 1, "worker", ServerTime);
        return lot;
    }

    private static ProductionOrder IssuedOrder(string id = "WO001") =>
        ProductionOrder.Create(id, "PLAN001", "EQ001", "PROD01", 100, SchedStart, SchedEnd).Value;

    // ── CreateLotAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLot_valid_data_persists_queued_lot()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync((Lot?)null);

        var result = await Build().CreateLotAsync(
            "P1", "LOT001", null, "PROD01", 10m, ["CUT", "ASSY"], "tester");

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(LotState.Queued, "웹 적응에서는 생성 즉시 공정 대기 상태다");
        _lots.Verify(r => r.AddAsync(It.IsAny<Lot>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateLot_duplicate_id_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());

        var result = await Build().CreateLotAsync(
            "P1", "LOT001", null, "PROD01", 10m, ["CUT"], "tester");

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.AddAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateLot_unknown_work_order_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync((Lot?)null);
        _orders.Setup(r => r.GetByIdAsync("WOXXX", default)).ReturnsAsync((ProductionOrder?)null);

        var result = await Build().CreateLotAsync(
            "P1", "LOT001", "WOXXX", "PROD01", 10m, ["CUT"], "tester");

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.AddAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateLot_product_mismatch_with_work_order_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync((Lot?)null);
        _orders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(IssuedOrder());

        var result = await Build().CreateLotAsync(
            "P1", "LOT001", "WO001", "OTHER", 10m, ["CUT"], "tester");

        result.IsFailure.Should().BeTrue("Lot 제품은 생산 지시 제품과 일치해야 한다");
    }

    [Fact]
    public async Task CreateLot_product_match_is_case_insensitive()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync((Lot?)null);
        _orders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(IssuedOrder());

        var result = await Build().CreateLotAsync(
            "P1", "LOT001", "WO001", "prod01", 10m, ["CUT"], "tester");

        result.IsSuccess.Should().BeTrue();
    }

    // ── TrackInAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TrackIn_queued_lot_succeeds_and_records_history()
    {
        var lot = QueuedLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", "RCP01", 1, "worker"));

        result.IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Processing);
        lot.EquipmentId.Should().Be("EQ001");
        _lots.Verify(r => r.UpdateAsync(lot, default), Times.Once);
        _histories.Verify(r => r.AddAsync(
            It.Is<LotHistory>(h => h.ExecutionId == LotExecutionId.TrackIn && h.LotId == "LOT001"),
            default), Times.Once);
    }

    [Fact]
    public async Task TrackIn_lot_not_found_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LXXX", "EQ001", null, null, "worker"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TrackIn_plant_mismatch_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());

        var result = await Build().TrackInAsync(
            new TrackInCommand("P2", "LOT001", "EQ001", null, null, "worker"));

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackIn_hold_lot_blocked_before_equipment_lookup()
    {
        var lot = QueuedLot();
        lot.Hold("admin");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsFailure.Should().BeTrue();
        _master.Verify(m => m.GetEquipmentAsync(It.IsAny<string>(), default), Times.Never,
            "설계 검증 순서상 Hold 차단이 설비 조회보다 먼저여야 한다");
    }

    [Fact]
    public async Task TrackIn_non_queued_lot_blocked_before_equipment_lookup()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(ProcessingLot());

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsFailure.Should().BeTrue();
        _master.Verify(m => m.GetEquipmentAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackIn_unknown_equipment_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());
        _master.Setup(m => m.GetEquipmentAsync("EQ404", default))
            .ReturnsAsync((TrackingEquipmentInfo?)null);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ404", null, null, "worker"));

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackIn_equipment_plant_mismatch_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());
        _master.Setup(m => m.GetEquipmentAsync("EQ001", default))
            .ReturnsAsync(new TrackingEquipmentInfo("EQ001", "P2", "CLS01", true));

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TrackIn_invalid_equipment_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());
        _master.Setup(m => m.GetEquipmentAsync("EQ001", default))
            .ReturnsAsync(new TrackingEquipmentInfo("EQ001", "P1", "CLS01", false));

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsFailure.Should().BeTrue("ValidState가 Valid가 아닌 설비는 TrackIn할 수 없다");
    }

    [Fact]
    public async Task TrackIn_without_recipe_skips_recipe_validation()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsSuccess.Should().BeTrue("Recipe 미지정은 현행 setIsUseValidationRecipe(false)의 적응으로 허용된다");
        _master.Verify(m => m.IsUsableRecipeAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackIn_unusable_recipe_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());
        _master.Setup(m => m.IsUsableRecipeAsync("RCP01", 1, "CLS01", default)).ReturnsAsync(false);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", "RCP01", 1, "worker"));

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackIn_validates_recipe_against_equipment_class()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot());

        await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", "RCP01", 2, "worker"));

        _master.Verify(m => m.IsUsableRecipeAsync("RCP01", 2, "CLS01", default), Times.Once,
            "Recipe는 TrackIn 설비의 클래스 기준으로 검증해야 한다");
    }

    [Fact]
    public async Task TrackIn_starts_issued_work_order()
    {
        var order = IssuedOrder();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot(workOrderId: "WO001"));
        _orders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(order);
        _orders.Setup(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default)).Returns(Task.CompletedTask);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(ProductionOrderStatus.InProgress, "첫 TrackIn 시 Issued 작업지시는 자동 시작된다");
        _orders.Verify(r => r.UpdateAsync(order, default), Times.Once);
    }

    [Fact]
    public async Task TrackIn_keeps_in_progress_work_order_untouched()
    {
        var order = IssuedOrder();
        order.Start(ServerTime);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot(workOrderId: "WO001"));
        _orders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(order);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsSuccess.Should().BeTrue();
        _orders.Verify(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackIn_succeeds_even_when_work_order_missing()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot(workOrderId: "WO404"));
        _orders.Setup(r => r.GetByIdAsync("WO404", default)).ReturnsAsync((ProductionOrder?)null);

        var result = await Build().TrackInAsync(
            new TrackInCommand("P1", "LOT001", "EQ001", null, null, "worker"));

        result.IsSuccess.Should().BeTrue("W/O 자동 시작은 best-effort — 실패해도 TrackIn은 유지된다");
    }

    // ── TrackOutAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TrackOut_mid_route_moves_to_next_step()
    {
        var lot = ProcessingLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ001", 10m, null, null, "worker"));

        result.IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Queued, "중간 공정 TrackOut은 다음 공정 대기로 돌아간다");
        lot.CurrentStepIndex.Should().Be(1);
        _lots.Verify(r => r.UpdateAsync(lot, default), Times.Once);
    }

    [Fact]
    public async Task TrackOut_history_keeps_equipment_before_release()
    {
        var recorded = new List<LotHistory>();
        var lot = ProcessingLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _histories.Setup(r => r.AddAsync(It.IsAny<LotHistory>(), default))
            .Callback<LotHistory, CancellationToken>((h, _) => recorded.Add(h))
            .Returns(Task.CompletedTask);

        await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ001", 10m, null, null, "worker"));

        lot.EquipmentId.Should().BeNull("TrackOut은 설비 점유를 반납한다");
        var history = recorded.Should().ContainSingle(h => h.ExecutionId == LotExecutionId.TrackOut).Subject;
        history.EquipmentId.Should().Be("EQ001", "이력에는 반납 전 설비가 남아야 추적이 가능하다");
        history.RecipeDefId.Should().Be("RCP01");
        history.ProcessId.Should().Be("CUT", "이력 공정은 TrackOut 시점의 공정이어야 한다");
    }

    [Fact]
    public async Task TrackOut_last_step_records_finish_history()
    {
        var lot = ProcessingLot(steps: "CUT");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ001", 10m, null, null, "worker"));

        result.IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Completed);
        _histories.Verify(r => r.AddAsync(
            It.Is<LotHistory>(h => h.ExecutionId == LotExecutionId.Finish), default), Times.Once);
        _histories.Verify(r => r.AddAsync(It.IsAny<LotHistory>(), default), Times.Exactly(2),
            "마지막 공정은 TrackOut + Finish 두 건이 기록돼야 한다");
    }

    [Fact]
    public async Task TrackOut_negative_defect_qty_rejected_before_master_lookup()
    {
        var lot = ProcessingLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackOutAsync(new TrackOutCommand(
            "P1", "LOT001", "EQ001", 10m, [new DefectEntry("D1", -1)], null, "worker"));

        result.IsFailure.Should().BeTrue();
        lot.State.Should().Be(LotState.Processing, "검증 실패 시 상태가 변하면 안 된다");
        _master.Verify(m => m.IsValidDefectCodeAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackOut_duplicate_defect_codes_fail_case_insensitively()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(ProcessingLot());

        var result = await Build().TrackOutAsync(new TrackOutCommand(
            "P1", "LOT001", "EQ001", 10m,
            [new DefectEntry("D1", 1), new DefectEntry("d1", 2)], null, "worker"));

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackOut_invalid_defect_code_fails()
    {
        var lot = ProcessingLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _master.Setup(m => m.IsValidDefectCodeAsync("BAD", default)).ReturnsAsync(false);

        var result = await Build().TrackOutAsync(new TrackOutCommand(
            "P1", "LOT001", "EQ001", 10m, [new DefectEntry("BAD", 1)], null, "worker"));

        result.IsFailure.Should().BeTrue("QMS 불량 분류에 없는 코드는 거부해야 한다");
        lot.State.Should().Be(LotState.Processing);
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackOut_defect_total_recorded_in_history_and_lot()
    {
        var lot = ProcessingLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackOutAsync(new TrackOutCommand(
            "P1", "LOT001", "EQ001", 10m,
            [new DefectEntry("D1", 1), new DefectEntry("D2", 2)], null, "worker"));

        result.IsSuccess.Should().BeTrue();
        lot.DefectQty.Should().Be(3m);
        _histories.Verify(r => r.AddAsync(
            It.Is<LotHistory>(h => h.ExecutionId == LotExecutionId.TrackOut && h.DefectQty == 3m),
            default), Times.Once);
    }

    [Fact]
    public async Task TrackOut_lot_not_found_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LXXX", "EQ001", 10m, null, null, "worker"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TrackOut_plant_mismatch_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(ProcessingLot());

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P2", "LOT001", "EQ001", 10m, null, null, "worker"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task TrackOut_equipment_mismatch_fails()
    {
        var lot = ProcessingLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ999", 10m, null, null, "worker"));

        result.IsFailure.Should().BeTrue("TrackIn 설비와 다른 설비로는 TrackOut할 수 없다");
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task TrackOut_completes_work_order_when_all_siblings_done()
    {
        var lot = ProcessingLot(workOrderId: "WO001", steps: "CUT");
        var consumed = QueuedLot("LOT002", "WO001", 5m);
        consumed.Consume("tester");
        var order = IssuedOrder();
        order.Start(ServerTime);

        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default))
            .ReturnsAsync(new List<Lot> { lot, consumed });
        _orders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(order);
        _orders.Setup(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default)).Returns(Task.CompletedTask);

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ001", 8m, null, null, "worker"));

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(ProductionOrderStatus.Completed, "진행 중 Lot이 없으면 W/O는 자동 마감된다");
        order.ActualQty.Should().Be(8m, "실적 수량은 Completed Lot 합계만 집계한다 (Consumed 제외)");
        _orders.Verify(r => r.UpdateAsync(order, default), Times.Once);
    }

    [Fact]
    public async Task TrackOut_skips_work_order_finish_when_sibling_in_progress()
    {
        var lot = ProcessingLot(workOrderId: "WO001", steps: "CUT");
        var sibling = QueuedLot("LOT002", "WO001", 5m);

        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default))
            .ReturnsAsync(new List<Lot> { lot, sibling });

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ001", 10m, null, null, "worker"));

        result.IsSuccess.Should().BeTrue();
        _orders.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never,
            "진행 중 sibling이 있으면 W/O 마감을 시도하지 않는다");
    }

    [Fact]
    public async Task TrackOut_skips_work_order_finish_when_order_not_in_progress()
    {
        var lot = ProcessingLot(workOrderId: "WO001", steps: "CUT");
        var order = IssuedOrder(); // Issued 상태 그대로 (수동 운영 중)

        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _lots.Setup(r => r.GetByWorkOrderAsync("WO001", default)).ReturnsAsync(new List<Lot> { lot });
        _orders.Setup(r => r.GetByIdAsync("WO001", default)).ReturnsAsync(order);

        var result = await Build().TrackOutAsync(
            new TrackOutCommand("P1", "LOT001", "EQ001", 10m, null, null, "worker"));

        result.IsSuccess.Should().BeTrue("자동 마감 실패는 TrackOut 자체에 영향을 주지 않는다");
        order.Status.Should().Be(ProductionOrderStatus.Issued);
        _orders.Verify(r => r.UpdateAsync(It.IsAny<ProductionOrder>(), default), Times.Never);
    }

    // ── MixingTrackInOutAsync ─────────────────────────────────────────────────

    private static MixingTrackCommand MixingCommand(params MixingInput[] inputs) =>
        new("P1", "MIX001", "PROD-MIX", "EQ001", ["MIX"], inputs, "worker");

    [Fact]
    public async Task Mixing_empty_inputs_fails()
    {
        var result = await Build().MixingTrackInOutAsync(MixingCommand());

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Mixing_non_positive_input_qty_fails()
    {
        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 0)));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Mixing_duplicate_input_lots_fail_case_insensitively()
    {
        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 1), new MixingInput("lot001", 2)));

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Mixing_unknown_input_lot_fails_without_changes()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LXXX", 1)));

        result.IsFailure.Should().BeTrue();
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
        _lots.Verify(r => r.AddAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task Mixing_hold_input_lot_fails()
    {
        var input = QueuedLot();
        input.Hold("admin");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(input);

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 1)));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Mixing_input_qty_exceeding_lot_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(QueuedLot(qty: 5m));

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 6m)));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Mixing_input_plant_mismatch_fails()
    {
        var other = Lot.Create("LOT001", "P2", null, "PROD01", 10m, ["CUT"], "tester").Value;
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(other);

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 1)));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Mixing_second_input_invalid_leaves_first_unchanged()
    {
        var first = QueuedLot("LOT001");
        var second = QueuedLot("LOT002");
        second.Hold("admin");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(first);
        _lots.Setup(r => r.GetByIdAsync("LOT002", default)).ReturnsAsync(second);

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 1), new MixingInput("LOT002", 1)));

        result.IsFailure.Should().BeTrue();
        first.State.Should().Be(LotState.Queued, "전수 검증 통과 전에는 어떤 Lot도 소비되면 안 된다 (UnitOfWork 부재 적응)");
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
        _lots.Verify(r => r.MixingPersistAsync(It.IsAny<MixingPersistPlan>(), default), Times.Never,
            "검증 실패 시 원자 배치조차 호출되면 안 된다(DATA-3)");
    }

    [Fact]
    public async Task Mixing_new_output_lot_created_and_completed()
    {
        var in1 = QueuedLot("LOT001", qty: 6m);
        var in2 = QueuedLot("LOT002", qty: 4m);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(in1);
        _lots.Setup(r => r.GetByIdAsync("LOT002", default)).ReturnsAsync(in2);
        _lots.Setup(r => r.GetByIdAsync("MIX001", default)).ReturnsAsync((Lot?)null);

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 6m), new MixingInput("LOT002", 4m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("MIX001");
        result.Value.Qty.Should().Be(10m, "출력 Lot 수량은 투입 합계다");
        result.Value.State.Should().Be(LotState.Completed, "단일 공정 Mixing은 TrackIn->TrackOut 연속 수행으로 완료된다");
        in1.State.Should().Be(LotState.Consumed);
        in2.State.Should().Be(LotState.Consumed);
        // DATA-3: 개별 AddAsync/UpdateAsync 대신 단일 원자 배치로 영속한다.
        _lots.Verify(r => r.MixingPersistAsync(
            It.Is<MixingPersistPlan>(p => p.IsNewOutput && p.Output.Id == "MIX001" && p.ConsumedInputs.Count == 2),
            default), Times.Once);
        _lots.Verify(r => r.AddAsync(It.IsAny<Lot>(), default), Times.Never);
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task Mixing_existing_output_lot_increases_qty()
    {
        var input = QueuedLot("LOT001", qty: 3m);
        var existing = Lot.Create("MIX001", "P1", null, "PROD-MIX", 5m, ["MIX"], "tester").Value;
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(input);
        _lots.Setup(r => r.GetByIdAsync("MIX001", default)).ReturnsAsync(existing);

        var result = await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 3m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Qty.Should().Be(8m, "기존 Mixing 출력 Lot에는 투입 합계를 가산한다 (설계 19.4.7)");
        _lots.Verify(r => r.MixingPersistAsync(
            It.Is<MixingPersistPlan>(p => !p.IsNewOutput && p.Output.Id == "MIX001"), default), Times.Once,
            "기존 출력 Lot 재사용 시 배치 계획은 UPDATE 경로(IsNewOutput=false)여야 한다");
    }

    [Fact]
    public async Task Mixing_records_relations_with_rate()
    {
        MixingPersistPlan? plan = null;
        var in1 = QueuedLot("LOT001", qty: 6m);
        var in2 = QueuedLot("LOT002", qty: 4m);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(in1);
        _lots.Setup(r => r.GetByIdAsync("LOT002", default)).ReturnsAsync(in2);
        _lots.Setup(r => r.GetByIdAsync("MIX001", default)).ReturnsAsync((Lot?)null);
        _lots.Setup(r => r.MixingPersistAsync(It.IsAny<MixingPersistPlan>(), default))
            .Callback<MixingPersistPlan, CancellationToken>((p, _) => plan = p)
            .Returns(Task.CompletedTask);

        await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 6m), new MixingInput("LOT002", 2m)));

        plan.Should().NotBeNull("성공 Mixing은 단일 원자 배치로 영속돼야 한다(DATA-3)");
        plan!.Relations.Should().HaveCount(2);
        plan.Relations.Should().AllSatisfy(r =>
        {
            r.OutputLotId.Should().Be("MIX001");
            r.ConsumedBy.Should().Be("worker");
        });
        plan.Relations.Single(r => r.InputLotId == "LOT001").MixingRate.Should().Be(0.75m);
        plan.Relations.Single(r => r.InputLotId == "LOT002").MixingRate.Should().Be(0.25m);
        plan.Histories.Should().NotBeEmpty("Consume/TrackIn/TrackOut 이력 스냅샷이 계획에 포함돼야 한다");
    }

    [Fact]
    public async Task Mixing_records_consume_and_output_histories()
    {
        // DATA-3: 이력은 개별 AddAsync가 아니라 원자 배치 계획(plan.Histories)에 스냅샷으로 담긴다.
        MixingPersistPlan? plan = null;
        var in1 = QueuedLot("LOT001", qty: 6m);
        var in2 = QueuedLot("LOT002", qty: 4m);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(in1);
        _lots.Setup(r => r.GetByIdAsync("LOT002", default)).ReturnsAsync(in2);
        _lots.Setup(r => r.GetByIdAsync("MIX001", default)).ReturnsAsync((Lot?)null);
        _lots.Setup(r => r.MixingPersistAsync(It.IsAny<MixingPersistPlan>(), default))
            .Callback<MixingPersistPlan, CancellationToken>((p, _) => plan = p)
            .Returns(Task.CompletedTask);

        await Build().MixingTrackInOutAsync(
            MixingCommand(new MixingInput("LOT001", 6m), new MixingInput("LOT002", 4m)));

        var recorded = plan!.Histories;
        recorded.Count(h => h.ExecutionId == LotExecutionId.Consume).Should().Be(2, "투입 Lot마다 Consume 이력이 남아야 한다");
        recorded.Count(h => h.ExecutionId == LotExecutionId.TrackIn && h.LotId == "MIX001").Should().Be(1);
        recorded.Count(h => h.ExecutionId == LotExecutionId.TrackOut && h.LotId == "MIX001").Should().Be(1);
        recorded.Count(h => h.ExecutionId == LotExecutionId.Finish && h.LotId == "MIX001").Should().Be(1,
            "단일 공정 출력 Lot은 Completed로 끝나므로 Finish 이력이 남는다");
        _histories.Verify(r => r.AddAsync(It.IsAny<LotHistory>(), default), Times.Never,
            "이력은 개별 INSERT가 아닌 원자 배치로만 기록된다");
    }

    // ── Hold / ReleaseHold ────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_queued_lot_succeeds()
    {
        var lot = QueuedLot();
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().HoldAsync("LOT001", "admin");

        result.IsSuccess.Should().BeTrue();
        lot.IsHold.Should().BeTrue();
        _lots.Verify(r => r.UpdateAsync(lot, default), Times.Once);
    }

    [Fact]
    public async Task Hold_completed_lot_fails()
    {
        var lot = ProcessingLot(steps: "CUT");
        lot.TrackOut("EQ001", 10m, 0, null, "worker", ServerTime);
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().HoldAsync("LOT001", "admin");

        result.IsFailure.Should().BeTrue("종결된 Lot은 Hold할 수 없다");
        _lots.Verify(r => r.UpdateAsync(It.IsAny<Lot>(), default), Times.Never);
    }

    [Fact]
    public async Task Hold_not_found_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await Build().HoldAsync("LXXX", "admin");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseHold_clears_hold()
    {
        var lot = QueuedLot();
        lot.Hold("admin");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);

        var result = await Build().ReleaseHoldAsync("LOT001", "admin");

        result.IsSuccess.Should().BeTrue();
        lot.IsHold.Should().BeFalse();
        _lots.Verify(r => r.UpdateAsync(lot, default), Times.Once);
    }

    // ── 조회 / 보고서 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLots_requires_plant_id()
    {
        var result = await Build().GetLotsAsync("  ");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetRoute_not_found_fails()
    {
        _lots.Setup(r => r.GetByIdAsync("LXXX", default)).ReturnsAsync((Lot?)null);

        var result = await Build().GetRouteAsync("LXXX");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetRoute_returns_lot_with_histories_and_mixings()
    {
        var lot = QueuedLot();
        var history = LotHistory.Of(lot, LotExecutionId.TrackIn, "worker", 10m, 0);
        var mixing = new LotMixingRelation("P1", "LOT001", "LOT-IN", 3m, 0.3m, ServerTime, "worker");
        _lots.Setup(r => r.GetByIdAsync("LOT001", default)).ReturnsAsync(lot);
        _histories.Setup(r => r.GetByLotAsync("P1", "LOT001", default))
            .ReturnsAsync(new List<LotHistory> { history });
        _mixings.Setup(r => r.GetByOutputLotAsync("P1", "LOT001", default))
            .ReturnsAsync(new List<LotMixingRelation> { mixing });

        var result = await Build().GetRouteAsync("LOT001");

        result.IsSuccess.Should().BeTrue();
        result.Value.Lot.Should().BeSameAs(lot);
        result.Value.Histories.Should().ContainSingle();
        result.Value.MixingInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task Report_requires_plant_id()
    {
        var result = await Build().GetTrackingReportAsync("", null, null, null, null, null);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Report_from_after_to_fails()
    {
        var result = await Build().GetTrackingReportAsync(
            "P1", null, null, null, ServerTime.AddDays(1), ServerTime);

        result.IsFailure.Should().BeTrue("조회 시작이 종료보다 늦을 수 없다");
    }

    [Theory]
    [InlineData(99999, LotTrackingService.MaxReportRows)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task Report_clamps_max_rows(int requested, int expected)
    {
        _histories.Setup(r => r.SearchAsync(
                "P1", null, null, null, null, null, expected, default))
            .ReturnsAsync(new List<LotHistory>());

        var result = await Build().GetTrackingReportAsync(
            "P1", null, null, null, null, null, requested);

        result.IsSuccess.Should().BeTrue();
        _histories.Verify(r => r.SearchAsync(
            "P1", null, null, null, null, null, expected, default), Times.Once);
    }

    [Fact]
    public async Task Report_trims_filters_and_converts_blank_to_null()
    {
        _histories.Setup(r => r.SearchAsync(
                "P1", "L1", null, null, null, null, LotTrackingService.DefaultReportRows, default))
            .ReturnsAsync(new List<LotHistory>());

        var result = await Build().GetTrackingReportAsync(
            " P1 ", " L1 ", "  ", null, null, null);

        result.IsSuccess.Should().BeTrue();
        _histories.Verify(r => r.SearchAsync(
            "P1", "L1", null, null, null, null, LotTrackingService.DefaultReportRows, default), Times.Once);
    }
}
