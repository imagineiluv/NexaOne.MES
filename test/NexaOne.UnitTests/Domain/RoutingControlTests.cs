using NexaOne.POM.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class RoutingControlTests
{
    private static readonly DateTime Now = new(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);

    private static Lot LotAtStep(int step, RoutingControlMode mode = RoutingControlMode.Strict)
    {
        var lot = Lot.Create(
            "LOT-R", "P1", null, "ITEM1", 10m,
            ["OP10", "OP20", "OP30", "OP40"], "planner", mode).Value;
        for (var index = 0; index < step; index++)
        {
            lot.TrackIn($"EQ{index}", null, null, "operator", Now.AddMinutes(index * 2));
            lot.TrackOut($"EQ{index}", 10m, 0m, null, "operator", Now.AddMinutes(index * 2 + 1));
        }
        lot.ClearDomainEvents();
        return lot;
    }

    [Fact]
    public void Strict_blocks_deviation_but_allows_normal_current_step()
    {
        var lot = LotAtStep(1);
        var policy = new RoutingPolicyEvaluator();

        policy.Evaluate(lot, RouteDeviationType.Normal, 1, null, null, Now)
            .Kind.Should().Be(RoutingDecisionKind.Allow);
        policy.Evaluate(lot, RouteDeviationType.Bypass, 3, "urgent", null, Now)
            .Code.Should().Be("ROUTE_STRICT_BLOCKED");
    }

    [Fact]
    public void NoControl_requires_reason_and_never_relaxes_hold()
    {
        var lot = LotAtStep(1, RoutingControlMode.NoControl);
        var policy = new RoutingPolicyEvaluator();

        policy.Evaluate(lot, RouteDeviationType.Bypass, 3, null, null, Now)
            .Code.Should().Be("ROUTE_REASON_REQUIRED");
        policy.Evaluate(lot, RouteDeviationType.Bypass, 3, "bottleneck", null, Now)
            .Kind.Should().Be(RoutingDecisionKind.AllowWithWarning);

        lot.Hold("quality");
        policy.Evaluate(lot, RouteDeviationType.Bypass, 3, "bottleneck", null, Now)
            .Code.Should().Be("ROUTE_HARD_INVARIANT");
    }

    [Theory]
    [InlineData(RouteDeviationType.Alternative)]
    [InlineData(RouteDeviationType.SequenceChange)]
    public void Alternative_and_sequence_change_move_selected_step_without_losing_current(
        RouteDeviationType type)
    {
        var lot = LotAtStep(1, RoutingControlMode.NoControl);

        lot.ApplyRouteDeviation(type, 2, "equipment availability", "supervisor")
            .IsSuccess.Should().BeTrue();

        lot.RouteSteps.Should().Equal("OP10", "OP30", "OP20", "OP40");
        lot.CurrentStepIndex.Should().Be(1);
        lot.CurrentProcessId.Should().Be("OP30");
        lot.NextProcessId.Should().Be("OP20");
    }

    [Fact]
    public void Bypass_moves_forward_and_keeps_original_route_snapshot_order()
    {
        var lot = LotAtStep(1, RoutingControlMode.NoControl);

        lot.ApplyRouteDeviation(RouteDeviationType.Bypass, 3, "optional coating", "supervisor")
            .IsSuccess.Should().BeTrue();

        lot.CurrentStepIndex.Should().Be(3);
        lot.RouteSteps.Should().Equal("OP10", "OP20", "OP30", "OP40");
    }

    [Fact]
    public void Running_lot_can_rework_and_trackout_returns_to_bound_source_once()
    {
        var lot = LotAtStep(2, RoutingControlMode.NoControl);
        lot.TrackIn("EQ-QA", "RCP-QA", 1, "operator", Now.AddHours(1));

        lot.ApplyRouteDeviation(RouteDeviationType.Rework, 0, "inspection failed", "supervisor")
            .IsSuccess.Should().BeTrue();
        lot.CurrentStepIndex.Should().Be(0);
        lot.ReturnStepIndex.Should().Be(2);
        lot.EquipmentId.Should().BeNull("Rework 전환은 실패 공정의 설비 점유를 해제한다");
        lot.State.Should().Be(LotState.Queued);

        lot.TrackIn("EQ-RW", null, null, "operator", Now.AddHours(2));
        lot.TrackOut("EQ-RW", 10m, 0m, null, "operator", Now.AddHours(3));

        lot.CurrentStepIndex.Should().Be(2);
        lot.CurrentProcessId.Should().Be("OP30");
        lot.ReturnStepIndex.Should().BeNull();
        lot.State.Should().Be(LotState.Queued);
        lot.DomainEvents.OfType<LotRouteDeviationAppliedDomainEvent>()
            .Should().ContainSingle(e => e.DeviationType == RouteDeviationType.Return);
    }

    [Fact]
    public void Flexible_approval_is_self_approval_safe_version_and_process_bound_and_one_time()
    {
        var lot = LotAtStep(1, RoutingControlMode.Flexible);
        var requested = RouteExceptionRequest.Request(
            "EX1", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            1, 3, "OP20", "OP40", lot.VersionNo, "urgent delivery",
            "operator", Now, Now.AddHours(1), "POP", "KIOSK-01").Value;

        requested.Approve("operator", null, Now.AddMinutes(1)).IsFailure.Should().BeTrue();
        requested.Reject("operator", "cancel", Now.AddMinutes(1)).IsFailure.Should().BeTrue();
        requested.Approve("supervisor", "approved", Now.AddMinutes(2)).IsSuccess.Should().BeTrue();
        requested.ValidateForApplication(lot, RouteDeviationType.Bypass, 1, 3, Now.AddMinutes(3))
            .IsSuccess.Should().BeTrue();
        requested.ValidateForApplication(lot, RouteDeviationType.Bypass, 1, 2, Now.AddMinutes(3))
            .IsFailure.Should().BeTrue();

        requested.MarkApplied("operator", "EXEC1", Now.AddMinutes(4)).IsSuccess.Should().BeTrue();
        requested.MarkApplied("operator", "EXEC2", Now.AddMinutes(5)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Manual_return_is_rejected_even_when_a_rework_return_point_exists()
    {
        var lot = LotAtStep(2, RoutingControlMode.NoControl);
        lot.ApplyRouteDeviation(RouteDeviationType.Rework, 0, "inspection failed", "supervisor")
            .IsSuccess.Should().BeTrue();

        lot.ApplyRouteDeviation(RouteDeviationType.Return, 2, "manual shortcut", "supervisor")
            .IsFailure.Should().BeTrue();
        lot.CurrentStepIndex.Should().Be(0);
        lot.ReturnStepIndex.Should().Be(2);
    }

    [Fact]
    public void Reject_after_expiry_projects_expired_instead_of_rejected()
    {
        var request = RouteExceptionRequest.Request(
            "EX-OLD", "LOT-R", "P1", RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "old request", "operator",
            Now.AddHours(-2), Now.AddHours(-1)).Value;

        request.Reject("supervisor", "too late", Now).IsFailure.Should().BeTrue();
        request.Status.Should().Be(RouteExceptionStatus.Expired);
    }
}
