using FluentAssertions;
using Moq;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

public sealed class PomLotRoutingMetaCommandDriverTests
{
    private static readonly MetaCommandExecutionContext MobileContext =
        new("POM_MOBILE_WORK_EXECUTION", "MOBILE", "PDA-07");

    [Fact]
    public async Task TrackIn_evaluates_normal_current_step_before_calling_typed_api()
    {
        PomEvaluateRoutingRequest? evaluated = null;
        PomLotTrackInRequest? tracked = null;
        var api = new Mock<IApiClient>();
        api.Setup(x => x.EvaluatePomLotRoutingAsync(
                "LOT-01", It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomEvaluateRoutingRequest, CancellationToken>((_, request, _) => evaluated = request)
            .ReturnsAsync(Decision("Allow", allowed: true));
        api.Setup(x => x.ExecutePomLotTrackInAsync(
                "LOT-01", It.IsAny<PomLotTrackInRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomLotTrackInRequest, CancellationToken>((_, request, _) => tracked = request)
            .ReturnsAsync(new PomRoutingApiResult<PomLotDto>(Lot(version: 4), null, 200));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(
            PomLotRoutingMetaCommands.TrackIn, Row(processState: "Idle"), MobileContext);

        result.Success.Should().BeTrue();
        evaluated.Should().NotBeNull();
        evaluated!.DeviationType.Should().Be("Normal");
        evaluated.TargetStepIndex.Should().Be(0);
        tracked.Should().NotBeNull();
        tracked!.ExpectedVersion.Should().Be(3);
        tracked.IdempotencyKey.Should().StartWith("meta:mobile:");
        tracked.IdempotencyKey.Length.Should().BeLessThanOrEqualTo(100);
        tracked.ClientChannel.Should().Be("MOBILE");
        tracked.DeviceId.Should().Be("PDA-07");
    }

    [Fact]
    public async Task TrackIn_preserves_structured_strict_block_reason_and_does_not_mutate()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.EvaluatePomLotRoutingAsync(
                "LOT-01", It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Decision(
                "Block", allowed: false, message: "이전 공정이 완료되지 않았습니다."));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(
            PomLotRoutingMetaCommands.TrackIn, Row(processState: "Idle"), MobileContext);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Error.Should().Contain("이전 공정");
        api.Verify(x => x.ExecutePomLotTrackInAsync(
            It.IsAny<string>(), It.IsAny<PomLotTrackInRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrackOut_uses_existing_domain_and_quality_gate_without_deviation_evaluation()
    {
        PomLotTrackOutRequest? tracked = null;
        var api = new Mock<IApiClient>();
        api.Setup(x => x.ExecutePomLotTrackOutAsync(
                "LOT-01", It.IsAny<PomLotTrackOutRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomLotTrackOutRequest, CancellationToken>((_, request, _) => tracked = request)
            .ReturnsAsync(new PomRoutingApiResult<PomLotDto>(Lot(version: 4), null, 200));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(
            PomLotRoutingMetaCommands.TrackOut, Row(processState: "Run"), MobileContext);

        result.Success.Should().BeTrue();
        tracked.Should().NotBeNull();
        tracked!.ClientChannel.Should().Be("MOBILE");
        tracked.DeviceId.Should().Be("PDA-07");
        api.Verify(x => x.EvaluatePomLotRoutingAsync(
            It.IsAny<string>(), It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrackOut_maps_repeated_defects_and_includes_them_in_typed_request()
    {
        PomLotTrackOutRequest? tracked = null;
        var api = new Mock<IApiClient>();
        api.Setup(x => x.ExecutePomLotTrackOutAsync(
                "LOT-01", It.IsAny<PomLotTrackOutRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomLotTrackOutRequest, CancellationToken>((_, request, _) => tracked = request)
            .ReturnsAsync(new PomRoutingApiResult<PomLotDto>(Lot(version: 4), null, 200));
        var row = Row("Run");
        row["DEFECTS"] = new List<Dictionary<string, object?>>
        {
            new() { ["DEFECT_CODE"] = "SCRATCH", ["DEFECT_QTY"] = 1.5m },
            new() { ["defectCode"] = "DENT", ["defectQty"] = "0.5" },
        };

        var result = await new PomLotRoutingMetaCommandDriver(api.Object).ExecuteAsync(
            PomLotRoutingMetaCommands.TrackOut, row, MobileContext);

        result.Success.Should().BeTrue();
        tracked!.Defects.Should().BeEquivalentTo(new[]
        {
            new PomLotDefectInput("SCRATCH", 1.5m),
            new PomLotDefectInput("DENT", 0.5m),
        });
    }

    [Fact]
    public void TrackOut_blocks_when_defect_total_exceeds_track_out_quantity()
    {
        var row = Row("Run");
        row["DEFECTS"] = new List<Dictionary<string, object?>>
        {
            new() { ["DEFECT_CODE"] = "SCRATCH", ["DEFECT_QTY"] = 11m },
        };

        var availability = new PomLotRoutingMetaCommandDriver(new Mock<IApiClient>().Object)
            .CanExecute(PomLotRoutingMetaCommands.TrackOut, row, MobileContext);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain("초과");
    }

    [Fact]
    public async Task Flexible_submit_creates_approval_request_instead_of_applying()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.EvaluatePomLotRoutingAsync(
                "LOT-01", It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Decision("ApprovalRequired", allowed: false));
        api.Setup(x => x.RequestPomLotRouteExceptionAsync(
                "LOT-01", It.IsAny<PomRequestRouteExceptionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PomRoutingApiResult<PomRouteExceptionDto>(Exception("Requested"), null, 200));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(
            PomLotRoutingMetaCommands.RequestException, DeviationRow(), MobileContext);

        result.Success.Should().BeTrue();
        api.Verify(x => x.RequestPomLotRouteExceptionAsync(
            "LOT-01", It.Is<PomRequestRouteExceptionRequest>(request =>
                request.ClientChannel == "MOBILE" && request.DeviceId == "PDA-07"
                && request.ExceptionId!.StartsWith("REX-")), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(x => x.ApplyPomLotRouteDeviationAsync(
            It.IsAny<string>(), It.IsAny<PomApplyRouteDeviationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Flexible_retry_uses_the_same_required_exception_id()
    {
        var ids = new List<string>();
        var api = new Mock<IApiClient>();
        api.Setup(x => x.EvaluatePomLotRoutingAsync(
                "LOT-01", It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Decision("ApprovalRequired", allowed: false));
        api.Setup(x => x.RequestPomLotRouteExceptionAsync(
                "LOT-01", It.IsAny<PomRequestRouteExceptionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomRequestRouteExceptionRequest, CancellationToken>((_, request, _) => ids.Add(request.ExceptionId))
            .ReturnsAsync(new PomRoutingApiResult<PomRouteExceptionDto>(Exception("Requested"), null, 200));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);

        await driver.ExecuteAsync(PomLotRoutingMetaCommands.RequestException, DeviationRow(), MobileContext);
        await driver.ExecuteAsync(PomLotRoutingMetaCommands.RequestException, DeviationRow(), MobileContext);

        ids.Should().HaveCount(2).And.OnlyContain(id => id == ids[0]);
        ids[0].Should().StartWith("REX-");
    }

    [Fact]
    public async Task Generated_idempotency_key_stays_within_database_limit_for_mobile_sequence_change()
    {
        PomApplyRouteDeviationRequest? applied = null;
        var api = new Mock<IApiClient>();
        api.Setup(x => x.EvaluatePomLotRoutingAsync(
                "LOT-01", It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Decision("AllowWithWarning", allowed: true));
        api.Setup(x => x.ApplyPomLotRouteDeviationAsync(
                "LOT-01", It.IsAny<PomApplyRouteDeviationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomApplyRouteDeviationRequest, CancellationToken>((_, request, _) => applied = request)
            .ReturnsAsync(new PomRoutingApiResult<PomLotDto>(Lot(version: 4), null, 200));
        var row = DeviationRow();
        row["DEVIATION_TYPE"] = "SequenceChange";
        row["TARGET_STEP_INDEX"] = int.MaxValue;

        var result = await new PomLotRoutingMetaCommandDriver(api.Object).ExecuteAsync(
            PomLotRoutingMetaCommands.RequestException, row, MobileContext);

        result.Success.Should().BeTrue();
        applied!.IdempotencyKey.Length.Should().BeLessThanOrEqualTo(100);
        applied.IdempotencyKey.Should().StartWith("meta:mobile:");
    }

    [Fact]
    public async Task NoControl_submit_applies_immediately_with_reason_and_audit_context()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.EvaluatePomLotRoutingAsync(
                "LOT-01", It.IsAny<PomEvaluateRoutingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Decision("AllowWithWarning", allowed: true));
        api.Setup(x => x.ApplyPomLotRouteDeviationAsync(
                "LOT-01", It.IsAny<PomApplyRouteDeviationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PomRoutingApiResult<PomLotDto>(Lot(version: 4), null, 200));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);
        var row = DeviationRow();
        row["EXCEPTION_ID"] = "STALE-APPROVAL";

        var result = await driver.ExecuteAsync(
            PomLotRoutingMetaCommands.RequestException, row, MobileContext);

        result.Success.Should().BeTrue();
        result.IsWarning.Should().BeTrue();
        result.Message.Should().NotBeNullOrWhiteSpace();
        api.Verify(x => x.ApplyPomLotRouteDeviationAsync(
            "LOT-01", It.Is<PomApplyRouteDeviationRequest>(request =>
                request.Reason == "설비 고장" && request.ClientChannel == "MOBILE"
                && request.DeviceId == "PDA-07" && request.ExceptionId == null), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(x => x.RequestPomLotRouteExceptionAsync(
            It.IsAny<string>(), It.IsAny<PomRequestRouteExceptionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Change_control_mode_uses_manage_command_and_dedicated_audit_reason()
    {
        PomChangeRoutingControlModeRequest? changed = null;
        var api = new Mock<IApiClient>();
        api.Setup(x => x.ChangePomLotRoutingControlModeAsync(
                "LOT-01", It.IsAny<PomChangeRoutingControlModeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, PomChangeRoutingControlModeRequest, CancellationToken>((_, request, _) => changed = request)
            .ReturnsAsync(new PomRoutingApiResult<PomLotDto>(Lot(version: 4), null, 200));
        var driver = new PomLotRoutingMetaCommandDriver(api.Object);
        var row = Row("Idle");
        row["CONTROL_MODE_TARGET"] = "Flexible";
        row["CONTROL_MODE_REASON"] = "긴급 납기 대응";

        var result = await driver.ExecuteAsync(
            PomLotRoutingMetaCommands.ChangeControlMode, row, MobileContext);

        result.Success.Should().BeTrue();
        changed.Should().NotBeNull();
        changed!.ControlMode.Should().Be("Flexible");
        changed.Reason.Should().Be("긴급 납기 대응");
        changed.ClientChannel.Should().Be("MOBILE");
        driver.Commands.Single(x => x.Id == PomLotRoutingMetaCommands.ChangeControlMode)
            .RequiredPermission.Should().Be("pom:manage");
    }

    [Fact]
    public void Return_is_internal_transition_and_cannot_be_submitted_from_screen_driver()
    {
        var driver = new PomLotRoutingMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = DeviationRow();
        row["DEVIATION_TYPE"] = "Return";

        var availability = driver.CanExecute(
            PomLotRoutingMetaCommands.RequestException, row, MobileContext);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain("Bypass").And.NotContain("Return");
    }

    [Fact]
    public void Descriptor_and_review_availability_keep_request_and_approval_permissions_separate()
    {
        var driver = new PomLotRoutingMetaCommandDriver(new Mock<IApiClient>().Object);

        driver.Commands.Single(x => x.Id == PomLotRoutingMetaCommands.RequestException)
            .RequiredPermission.Should().Be("pom:routing.request");
        driver.Commands.Single(x => x.Id == PomLotRoutingMetaCommands.ApproveException)
            .RequiredPermission.Should().Be("pom:routing.approve");
        driver.Commands.Single(x => x.Id == PomLotRoutingMetaCommands.Evaluate)
            .Effect.Should().Be(MetaCommandEffect.NonMutating);
        driver.Commands.Where(x => x.Id != PomLotRoutingMetaCommands.Evaluate)
            .Should().OnlyContain(x => x.Effect == MetaCommandEffect.Mutating);
        driver.CanExecute(
            PomLotRoutingMetaCommands.ApproveException,
            new Dictionary<string, object?> { ["EXCEPTION_ID"] = "REX-1", ["STATUS"] = "Requested" },
            MobileContext).CanExecute.Should().BeTrue();
        driver.CanExecute(
            PomLotRoutingMetaCommands.ApproveException,
            new Dictionary<string, object?> { ["EXCEPTION_ID"] = "REX-1", ["STATUS"] = "Approved" },
            MobileContext).DisabledReason.Should().Contain("Requested");
    }

    [Fact]
    public async Task Review_uses_reviewer_channel_and_device_context()
    {
        PomReviewRouteExceptionRequest? reviewed = null;
        var api = new Mock<IApiClient>();
        api.Setup(x => x.ReviewPomLotRouteExceptionAsync(
                "approve", "REX-1", It.IsAny<PomReviewRouteExceptionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PomReviewRouteExceptionRequest, CancellationToken>(
                (_, _, request, _) => reviewed = request)
            .ReturnsAsync(new PomRoutingApiResult<PomRouteExceptionDto>(Exception("Approved"), null, 200));
        var row = new Dictionary<string, object?>
        {
            ["EXCEPTION_ID"] = "REX-1",
            ["STATUS"] = "Requested",
            ["REVIEW_REASON"] = "검토 완료",
        };

        var result = await new PomLotRoutingMetaCommandDriver(api.Object).ExecuteAsync(
            PomLotRoutingMetaCommands.ApproveException, row, MobileContext);

        result.Success.Should().BeTrue();
        reviewed.Should().Be(new PomReviewRouteExceptionRequest(
            "검토 완료", "MOBILE", "PDA-07"));
    }

    private static PomRoutingApiResult<PomRoutingPolicyDecisionDto> Decision(
        string kind, bool allowed, string message = "라우팅 정책 판정")
        => new(new PomRoutingPolicyDecisionDto(
            kind, "ROUTE_TEST", message, kind == "ApprovalRequired" ? "Flexible" : "Strict",
            "Normal", 0, 0, false, null, allowed), null, 200);

    private static Dictionary<string, object?> Row(string processState)
        => new(StringComparer.Ordinal)
        {
            ["LOT_ID"] = "LOT-01",
            ["PLANT_ID"] = "P1",
            ["VERSION_NO"] = 3,
            ["CURRENT_STEP"] = 0,
            ["EQUIPMENT_ID"] = "EQ-01",
            ["QTY"] = 10m,
            ["PROCESS_STATE"] = processState,
            ["IS_HOLD"] = "N",
        };

    private static Dictionary<string, object?> DeviationRow()
    {
        var row = Row("Idle");
        row["DEVIATION_TYPE"] = "Bypass";
        row["TARGET_STEP_INDEX"] = 1;
        row["REASON"] = "설비 고장";
        return row;
    }

    private static PomLotDto Lot(int version)
        => new(
            "LOT-01", "P1", "WO-01", "ITEM-01", 10m, 0m,
            "Queued", "Idle", new[] { "CUT", "ASSY" }, 0, "CUT",
            null, null, null, null, false, version, "Strict",
            null, null, false, 1, "ASSY");

    private static PomRouteExceptionDto Exception(string status)
        => new(
            "REX-1", "LOT-01", "P1", "Bypass", 0, 1, "CUT", "ASSY", 3,
            "설비 고장", status, "operator", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30),
            null, null, null, null, null, null, "MOBILE", "PDA-07");
}
