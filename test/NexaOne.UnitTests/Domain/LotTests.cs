using System.Text.Json;
using NexaOne.POM.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class LotTests
{
    private static readonly DateTime ServerTime = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static Lot QueuedLot(decimal qty = 10m, params string[] steps) =>
        Lot.Create("LOT001", "P1", "ORD001", "PROD01",
            qty, steps.Length == 0 ? ["CUT", "ASSY"] : steps, "tester").Value;

    private static Lot ProcessingLot(decimal qty = 10m, params string[] steps)
    {
        var lot = QueuedLot(qty, steps);
        lot.TrackIn("EQ001", "RCP01", 1, "worker", ServerTime);
        return lot;
    }

    // ── LotStateMachine ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(LotState.Created, LotState.Queued, true)]
    [InlineData(LotState.Created, LotState.Consumed, true)]
    [InlineData(LotState.Created, LotState.Processing, false)]
    [InlineData(LotState.Queued, LotState.Processing, true)]
    [InlineData(LotState.Queued, LotState.Consumed, true)]
    [InlineData(LotState.Queued, LotState.Completed, false)]
    [InlineData(LotState.Processing, LotState.Queued, true)]
    [InlineData(LotState.Processing, LotState.Completed, true)]
    [InlineData(LotState.Processing, LotState.Consumed, false)]
    [InlineData(LotState.Completed, LotState.Queued, false)]
    [InlineData(LotState.Consumed, LotState.Queued, false)]
    public void StateMachine_allows_only_designed_transitions(LotState from, LotState to, bool expected)
        => LotStateMachine.CanTransition(from, to).Should().Be(expected,
            because: "설계 19.4.3의 상태 전이 규칙을 따라야 한다");

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_valid_lot_starts_queued()
    {
        var result = Lot.Create("LOT001", "P1", "ORD001", "PROD01", 10m, ["CUT", "ASSY"], "tester");

        result.IsSuccess.Should().BeTrue();
        var lot = result.Value;
        lot.State.Should().Be(LotState.Queued, because: "release 단계가 없는 웹 적응에서는 생성 즉시 공정 대기");
        lot.ProcessState.Should().Be(LotProcessState.Idle);
        lot.CurrentProcessId.Should().Be("CUT");
        lot.IsLastStep.Should().BeFalse();
        lot.CreatedBy.Should().Be("tester");
    }

    [Fact]
    public void Create_trims_and_drops_empty_route_steps()
    {
        var result = Lot.Create("LOT001", "P1", null, "PROD01", 10m, [" CUT ", "", "ASSY"], "tester");

        result.IsSuccess.Should().BeTrue();
        result.Value.RouteSteps.Should().Equal("CUT", "ASSY");
        result.Value.WorkOrderId.Should().BeNull();
    }

    [Theory]
    [InlineData("", "P1", "PROD01")]
    [InlineData("LOT001", "", "PROD01")]
    [InlineData("LOT001", "P1", "")]
    public void Create_missing_required_fields_fails(string lotId, string plantId, string productId)
        => Lot.Create(lotId, plantId, null, productId, 10m, ["CUT"], "tester")
            .IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_non_positive_qty_fails(decimal qty)
        => Lot.Create("LOT001", "P1", null, "PROD01", qty, ["CUT"], "tester")
            .IsFailure.Should().BeTrue();

    [Fact]
    public void Create_empty_route_fails()
        => Lot.Create("LOT001", "P1", null, "PROD01", 10m, [], "tester")
            .IsFailure.Should().BeTrue(because: "공정 경로는 최소 1개 공정이 필요하다");

    [Fact]
    public void Create_step_containing_separator_fails()
        => Lot.Create("LOT001", "P1", null, "PROD01", 10m, ["CUT>ASSY"], "tester")
            .IsFailure.Should().BeTrue(because: "'>'는 ROUTE_STEPS 직렬화 구분자라 공정 ID에 쓸 수 없다");

    [Fact]
    public void Create_route_exceeding_column_length_fails()
    {
        var steps = Enumerable.Range(0, 60).Select(i => $"PROC{i:D6}").ToList();
        Lot.Create("LOT001", "P1", null, "PROD01", 10m, steps, "tester")
            .IsFailure.Should().BeTrue(because: "직렬화 결과가 NVARCHAR(500)을 넘으면 안 된다");
    }

    // ── TrackIn ───────────────────────────────────────────────────────────────

    [Fact]
    public void TrackIn_queued_lot_succeeds()
    {
        var lot = QueuedLot();

        var result = lot.TrackIn("EQ001", "RCP01", 2, "worker", ServerTime);

        result.IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Processing);
        lot.ProcessState.Should().Be(LotProcessState.Run);
        lot.EquipmentId.Should().Be("EQ001");
        lot.RecipeDefId.Should().Be("RCP01");
        lot.RecipeDefVersion.Should().Be(2);
        lot.TrackInUser.Should().Be("worker");
        lot.TrackInTime.Should().Be(ServerTime);
        lot.TrackOutTime.Should().BeNull(because: "TrackIn 시 이전 TrackOut 기록은 초기화된다");
    }

    [Fact]
    public void TrackIn_hold_lot_fails()
    {
        var lot = QueuedLot();
        lot.Hold("manager");

        lot.TrackIn("EQ001", null, null, "worker", ServerTime).IsFailure.Should().BeTrue();
        lot.State.Should().Be(LotState.Queued);
    }

    [Fact]
    public void TrackIn_processing_lot_fails()
    {
        var lot = ProcessingLot();
        lot.TrackIn("EQ002", null, null, "worker", ServerTime)
            .IsFailure.Should().BeTrue(because: "Processing 중에는 다시 TrackIn할 수 없다");
    }

    [Fact]
    public void TrackIn_empty_equipment_fails()
        => QueuedLot().TrackIn(" ", null, null, "worker", ServerTime).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_rejects_route_step_longer_than_downstream_process_columns()
    {
        var result = Lot.Create(
            "LOT-LONG-STEP", "P1", null, "PROD01", 10m,
            [new string('P', Lot.MaxProcessIdLength + 1)], "planner");

        result.IsFailure.Should().BeTrue(
            "route steps are copied into multiple NVARCHAR(50) execution and history columns");
    }

    [Theory]
    [InlineData("0.00001")]
    [InlineData("100000000000000")]
    public void Create_rejects_quantity_outside_decimal_18_4(string raw)
    {
        var result = Lot.Create(
            "LOT-SQL-QTY", "P1", null, "PROD01", decimal.Parse(raw), ["CUT"], "planner");

        result.IsFailure.Should().BeTrue();
    }

    // ── TrackOut ──────────────────────────────────────────────────────────────

    [Fact]
    public void TrackOut_mid_route_moves_to_next_step()
    {
        var lot = ProcessingLot();

        var result = lot.TrackOut("EQ001", 9m, 1m, "CAR01", "worker", ServerTime.AddHours(1));

        result.IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Queued, because: "마지막 공정이 아니면 다음 공정 대기로 돌아간다");
        lot.CurrentStepIndex.Should().Be(1);
        lot.CurrentProcessId.Should().Be("ASSY");
        lot.Qty.Should().Be(9m);
        lot.DefectQty.Should().Be(1m);
        lot.CarrierId.Should().Be("CAR01");
        lot.EquipmentId.Should().BeNull(because: "TrackOut 시 설비 점유를 반납한다");
        lot.RecipeDefId.Should().BeNull();
        lot.ProcessState.Should().Be(LotProcessState.Idle);
    }

    [Fact]
    public void TrackOut_last_step_completes_lot()
    {
        var lot = ProcessingLot(qty: 10m, "CUT");

        lot.TrackOut("EQ001", 10m, 0m, null, "worker", ServerTime.AddHours(1)).IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Completed);
    }

    [Fact]
    public void TrackOut_different_equipment_fails()
    {
        var lot = ProcessingLot();
        lot.TrackOut("EQ999", 10m, 0m, null, "worker", ServerTime)
            .IsFailure.Should().BeTrue(because: "TrackIn 설비와 TrackOut 설비가 일치해야 한다");
    }

    [Fact]
    public void TrackOut_equipment_match_is_case_insensitive()
        => ProcessingLot().TrackOut("eq001", 10m, 0m, null, "worker", ServerTime)
            .IsSuccess.Should().BeTrue();

    [Fact]
    public void TrackOut_queued_lot_fails()
        => QueuedLot().TrackOut("EQ001", 10m, 0m, null, "worker", ServerTime)
            .IsFailure.Should().BeTrue();

    [Fact]
    public void TrackOut_qty_over_lot_qty_fails()
        => ProcessingLot(qty: 10m).TrackOut("EQ001", 11m, 0m, null, "worker", ServerTime)
            .IsFailure.Should().BeTrue();

    [Fact]
    public void TrackOut_defect_over_produced_qty_fails()
        => ProcessingLot(qty: 10m).TrackOut("EQ001", 5m, 6m, null, "worker", ServerTime)
            .IsFailure.Should().BeTrue();

    [Fact]
    public void TrackOut_negative_qty_fails()
        => ProcessingLot().TrackOut("EQ001", -1m, 0m, null, "worker", ServerTime)
            .IsFailure.Should().BeTrue();

    [Fact]
    public void TrackOut_zero_qty_fails_before_database_persistence()
    {
        var lot = ProcessingLot();

        var result = lot.TrackOut("EQ001", 0m, 0m, null, "worker", ServerTime);

        result.IsFailure.Should().BeTrue();
        lot.State.Should().Be(LotState.Processing);
        lot.Qty.Should().Be(10m);
    }

    [Fact]
    public void TrackOut_rejects_fraction_beyond_decimal_18_4()
    {
        var lot = ProcessingLot();

        var result = lot.TrackOut("EQ001", 9.99999m, 0m, null, "worker", ServerTime);

        result.IsFailure.Should().BeTrue();
        lot.State.Should().Be(LotState.Processing);
    }

    [Fact]
    public void TrackOut_rejects_carrier_id_longer_than_database_column()
    {
        var lot = ProcessingLot();

        var result = lot.TrackOut(
            "EQ001", 10m, 0m, new string('C', 51), "worker", ServerTime);

        result.IsFailure.Should().BeTrue();
        lot.State.Should().Be(LotState.Processing);
    }

    [Fact]
    public void TrackOut_hold_lot_fails()
    {
        var lot = ProcessingLot();
        lot.Hold("manager");
        lot.TrackOut("EQ001", 10m, 0m, null, "worker", ServerTime).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void TrackOut_accumulates_defect_qty_across_steps()
    {
        var lot = ProcessingLot(qty: 10m, "CUT", "ASSY");
        lot.TrackOut("EQ001", 9m, 1m, null, "worker", ServerTime);
        lot.TrackIn("EQ002", null, null, "worker", ServerTime.AddHours(2));

        lot.TrackOut("EQ002", 8m, 1m, null, "worker", ServerTime.AddHours(3));

        lot.DefectQty.Should().Be(2m, because: "불량 수량은 공정마다 누적된다");
        lot.State.Should().Be(LotState.Completed);
    }

    [Fact]
    public void TrackOut_rejects_when_existing_and_new_defects_exceed_current_qty()
    {
        var lot = ProcessingLot(qty: 10m, "CUT", "ASSY");
        lot.TrackOut("EQ001", 6m, 4m, null, "worker", ServerTime).IsSuccess.Should().BeTrue();
        lot.TrackIn("EQ002", null, null, "worker", ServerTime.AddHours(1)).IsSuccess.Should().BeTrue();

        var result = lot.TrackOut("EQ002", 5m, 2m, null, "worker", ServerTime.AddHours(2));

        result.IsFailure.Should().BeTrue("cumulative defects would be 6 while current quantity is 5");
        lot.State.Should().Be(LotState.Processing, "a validation failure must not partially mutate the lot");
        lot.Qty.Should().Be(6m);
        lot.DefectQty.Should().Be(4m);
        lot.CurrentStepIndex.Should().Be(1);
    }

    // ── Consume / IncreaseMixingQty (Mixing, 설계 19.4.7) ────────────────────

    [Fact]
    public void Consume_queued_lot_succeeds()
    {
        var lot = QueuedLot();

        lot.Consume("worker").IsSuccess.Should().BeTrue();
        lot.State.Should().Be(LotState.Consumed);
        lot.ProcessState.Should().Be(LotProcessState.Idle);
    }

    [Fact]
    public void Consume_processing_lot_fails()
        => ProcessingLot().Consume("worker")
            .IsFailure.Should().BeTrue(because: "생산 진행 중인 Lot은 Mixing 투입으로 소비할 수 없다");

    [Fact]
    public void Consume_hold_lot_fails()
    {
        var lot = QueuedLot();
        lot.Hold("manager");
        lot.Consume("worker").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void IncreaseMixingQty_queued_lot_adds_qty()
    {
        var lot = QueuedLot(qty: 10m);

        lot.IncreaseMixingQty(5m, "worker").IsSuccess.Should().BeTrue();
        lot.Qty.Should().Be(15m);
    }

    [Fact]
    public void IncreaseMixingQty_processing_lot_fails()
        => ProcessingLot().IncreaseMixingQty(5m, "worker").IsFailure.Should().BeTrue();

    [Fact]
    public void IncreaseMixingQty_non_positive_fails()
        => QueuedLot().IncreaseMixingQty(0m, "worker").IsFailure.Should().BeTrue();

    [Fact]
    public void IncreaseMixingQty_hold_lot_fails()
    {
        var lot = QueuedLot();
        lot.Hold("manager");
        lot.IncreaseMixingQty(5m, "worker").IsFailure.Should().BeTrue();
    }

    // ── Hold / ReleaseHold ────────────────────────────────────────────────────

    [Fact]
    public void Hold_then_release_restores_operations()
    {
        var lot = QueuedLot();
        lot.Hold("manager");
        lot.IsHold.Should().BeTrue();

        lot.ReleaseHold("manager");

        lot.IsHold.Should().BeFalse();
        lot.TrackIn("EQ001", null, null, "worker", ServerTime).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Hold_completed_lot_fails()
    {
        var lot = ProcessingLot(qty: 10m, "CUT");
        lot.TrackOut("EQ001", 10m, 0m, null, "worker", ServerTime);

        lot.Hold("manager").IsFailure.Should().BeTrue(because: "종결된 Lot은 Hold 대상이 아니다");
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    [Fact]
    public void Restore_rebuilds_saved_state_without_validation()
    {
        var lot = Lot.Restore(
            "LOT001", "P1", "ORD001", "PROD01", 8m, 2m,
            LotState.Processing, LotProcessState.Run,
            ["CUT", "ASSY"], 1, "EQ001", "RCP01", 3, "CAR01",
            isHold: true, "worker", ServerTime, null, null);

        lot.State.Should().Be(LotState.Processing);
        lot.CurrentProcessId.Should().Be("ASSY");
        lot.IsLastStep.Should().BeTrue();
        lot.IsHold.Should().BeTrue();
        lot.RecipeDefVersion.Should().Be(3);
    }

    // ── ADR-002 도메인 이벤트(→ outbox) ────────────────────────────────────────

    [Fact]
    public void TrackIn_raises_LotTrackedIn_event_with_outbox_envelope()
    {
        var lot = QueuedLot();

        lot.TrackIn("EQ001", "RCP01", 2, "worker", ServerTime).IsSuccess.Should().BeTrue();

        var ev = lot.DomainEvents.OfType<LotTrackedInDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("LotTrackedIn");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("LOT001", "Lot별 순서 보장을 위해 AGGREGATE_ID는 LOT_ID여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ001");
        doc.RootElement.GetProperty("RecipeDefId").GetString().Should().Be("RCP01");
        doc.RootElement.GetProperty("RecipeDefVersion").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("ProcessId").GetString().Should().Be("CUT", "진입한 현재 공정을 담는다");
    }

    [Fact]
    public void TrackOut_raises_LotTrackedOut_event_with_post_transition_state()
    {
        var lot = ProcessingLot();   // ["CUT","ASSY"] — 첫 TrackOut은 다음 공정 대기로 전이
        lot.ClearDomainEvents();     // TrackIn 이벤트 제거 — TrackOut 이벤트만 검증

        lot.TrackOut("EQ001", 9m, 1m, "CAR01", "worker", ServerTime.AddHours(1)).IsSuccess.Should().BeTrue();

        var ev = lot.DomainEvents.OfType<LotTrackedOutDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("LotTrackedOut");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("LOT001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("Qty").GetDecimal().Should().Be(9m);
        doc.RootElement.GetProperty("DefectQty").GetDecimal().Should().Be(1m);
        doc.RootElement.GetProperty("NewState").GetString().Should().Be("Queued", "마지막 공정이 아니면 다음 공정 대기로 전이된다");
        doc.RootElement.GetProperty("CurrentStepIndex").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("IsLastStep").GetBoolean().Should()
            .BeTrue("IsLastStep은 '전이 후' 위치 기준 — 다음 공정(인덱스 1)이 경로의 마지막 단계라 true다. 완료 여부는 NewState(Queued=미완료)로 구분한다");
    }

    [Fact]
    public void Consume_raises_LotConsumed_event_with_outbox_envelope()
    {
        var lot = QueuedLot(qty: 10m);

        lot.Consume("worker").IsSuccess.Should().BeTrue();

        var ev = lot.DomainEvents.OfType<LotConsumedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("LotConsumed");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("LOT001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("ProductId").GetString().Should().Be("PROD01");
        doc.RootElement.GetProperty("Qty").GetDecimal().Should().Be(10m);
    }

    [Fact]
    public void Hold_ReleaseHold_IncreaseMixingQty_raise_no_events()
    {
        var lot = QueuedLot();

        lot.IncreaseMixingQty(5m, "worker").IsSuccess.Should().BeTrue();
        lot.Hold("manager").IsSuccess.Should().BeTrue();
        lot.ReleaseHold("manager").IsSuccess.Should().BeTrue();

        lot.DomainEvents.Should().BeEmpty("Hold/ReleaseHold/IncreaseMixingQty는 도메인 이벤트 발행 대상이 아니다");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => Lot.Restore("LOT001", "P1", "ORD001", "PROD01", 8m, 2m, LotState.Processing, LotProcessState.Run,
                ["CUT", "ASSY"], 1, "EQ001", "RCP01", 3, "CAR01", isHold: false, "worker", ServerTime, null, null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var lot = QueuedLot();
        lot.TrackIn("EQ001", null, null, "worker", ServerTime);
        lot.ClearDomainEvents();
        lot.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
