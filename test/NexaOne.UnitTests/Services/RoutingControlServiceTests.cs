using Moq;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.UnitTests.Services;

public sealed class RoutingControlServiceTests
{
    private readonly Mock<ILotRepository> _lots = new();
    private readonly Mock<IAtomicLotRepository> _atomicLots = new();
    private readonly Mock<ILotHistoryRepository> _histories = new();
    private readonly Mock<ILotMixingRelationRepository> _mixings = new();
    private readonly Mock<IPomWorkOrderRepository> _workOrders = new();
    private readonly Mock<ITrackingMasterGateway> _master = new();
    private readonly Mock<IProductionQualityGateway> _quality = new();

    public RoutingControlServiceTests()
    {
        _master.Setup(m => m.GetEquipmentAsync("EQ", default))
            .ReturnsAsync(new TrackingEquipmentInfo("EQ", "P1", "CLASS1", true));
        _quality.Setup(q => q.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default))
            .ReturnsAsync(ProductionQualityGateResult.NotRequired());
        _atomicLots.Setup(r => r.GetExecutionAsync(It.IsAny<string>(), default))
            .ReturnsAsync((LotExecutionRecord?)null);
        _atomicLots.Setup(r => r.PersistTransitionAsync(It.IsAny<LotTransitionPersistPlan>(), default))
            .ReturnsAsync(LotTransitionPersistResult.Persisted);
    }

    private LotTrackingService Build(IRoutingPolicyEvaluator? evaluator = null) => evaluator is null
        ? new(_lots.Object, _atomicLots.Object, _histories.Object, _mixings.Object, _workOrders.Object, _master.Object, _quality.Object)
        : new(_lots.Object, _atomicLots.Object, _histories.Object, _mixings.Object, _workOrders.Object, _master.Object, _quality.Object, evaluator);

    private static Lot FlexibleLot() => Lot.Create(
        "LOT-FLEX", "P1", null, "ITEM1", 10m,
        ["OP10", "OP20", "OP30", "OP40"], "planner", RoutingControlMode.Flexible).Value;

    private (Mock<IRouteExceptionRepository> Exceptions, Mock<IAtomicLotRepository> Atomic) AddCapabilities()
    {
        var exceptions = _lots.As<IRouteExceptionRepository>();
        return (exceptions, _atomicLots);
    }

    [Fact]
    public async Task Exception_expiry_is_clamped_to_eight_hours_from_server_time()
    {
        var lot = FlexibleLot();
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, _) = AddCapabilities();
        RouteExceptionRequest? captured = null;
        exceptions.Setup(r => r.TryAddRouteExceptionAsync(It.IsAny<RouteExceptionRequest>(), default))
            .Callback<RouteExceptionRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(RouteExceptionAddResult.Added);
        var before = DateTime.UtcNow;

        var result = await Build().RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            "EX-LONG", "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, DateTime.UtcNow.AddHours(9)));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        captured.Should().NotBeNull();
        captured!.ExpiresAt.Should().BeOnOrBefore(before.AddHours(8).AddSeconds(1));
    }

    [Fact]
    public async Task Exception_id_retry_requires_same_requester_expiry_channel_and_device()
    {
        var lot = FlexibleLot();
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, _) = AddCapabilities();
        var now = DateTime.UtcNow;
        var expiry = now.AddHours(1);
        var existing = RouteExceptionRequest.Request(
            "EX-IDEMP", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "urgent", "operator",
            now, expiry, "MOBILE", "PDA-01").Value;
        exceptions.Setup(r => r.GetRouteExceptionAsync(existing.Id, default)).ReturnsAsync(existing);

        var exact = new RequestRouteExceptionCommand(
            existing.Id, "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, expiry, "MOBILE", "PDA-01");

        (await Build().RequestRouteExceptionAsync(exact)).IsSuccess.Should().BeTrue();
        (await Build().RequestRouteExceptionAsync(exact with { User = "other" })).IsFailure.Should().BeTrue();
        (await Build().RequestRouteExceptionAsync(exact with { ExpiresAt = expiry.AddMinutes(1) })).IsSuccess.Should().BeTrue(
            "effective expiry is server-owned and must not break an exact UI retry");
        (await Build().RequestRouteExceptionAsync(exact with { ClientChannel = "POP" })).IsFailure.Should().BeTrue();
        (await Build().RequestRouteExceptionAsync(exact with { DeviceId = "PDA-02" })).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_duplicate_insert_reloads_and_returns_the_exact_existing_request()
    {
        var lot = FlexibleLot();
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, _) = AddCapabilities();
        var now = DateTime.UtcNow;
        var concurrent = RouteExceptionRequest.Request(
            "EX-RACE", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "urgent", "operator",
            now, now.AddHours(1)).Value;
        exceptions.SetupSequence(r => r.GetRouteExceptionAsync(concurrent.Id, default))
            .ReturnsAsync((RouteExceptionRequest?)null)
            .ReturnsAsync(concurrent);
        exceptions.Setup(r => r.TryAddRouteExceptionAsync(It.IsAny<RouteExceptionRequest>(), default))
            .ReturnsAsync(RouteExceptionAddResult.AlreadyExists);

        var result = await Build().RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            concurrent.Id, "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, now.AddHours(1)));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        result.Value.Should().BeSameAs(concurrent);
    }

    [Fact]
    public async Task Concurrent_duplicate_insert_with_different_request_returns_conflict()
    {
        var lot = FlexibleLot();
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, _) = AddCapabilities();
        var now = DateTime.UtcNow;
        var concurrent = RouteExceptionRequest.Request(
            "EX-RACE-CONFLICT", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "different request", "other-operator",
            now, now.AddHours(1)).Value;
        exceptions.SetupSequence(r => r.GetRouteExceptionAsync(concurrent.Id, default))
            .ReturnsAsync((RouteExceptionRequest?)null)
            .ReturnsAsync(concurrent);
        exceptions.Setup(r => r.TryAddRouteExceptionAsync(It.IsAny<RouteExceptionRequest>(), default))
            .ReturnsAsync(RouteExceptionAddResult.AlreadyExists);

        var result = await Build().RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            concurrent.Id, "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, now.AddHours(1)));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("created concurrently");
    }

    [Fact]
    public async Task Route_exception_insert_fault_is_not_treated_as_an_identity_race()
    {
        var lot = FlexibleLot();
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, _) = AddCapabilities();
        var repositoryFault = new InvalidOperationException("database unavailable");
        exceptions.Setup(r => r.TryAddRouteExceptionAsync(
                It.IsAny<RouteExceptionRequest>(), default))
            .ThrowsAsync(repositoryFault);

        var act = () => Build().RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            "EX-FAULT", "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, DateTime.UtcNow.AddHours(1)));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(repositoryFault);
        exceptions.Verify(r => r.GetRouteExceptionAsync("EX-FAULT", default), Times.Once,
            "an arbitrary write fault must not enter the duplicate-key reload path");
    }

    [Fact]
    public async Task Route_exception_insert_cancellation_propagates()
    {
        var lot = FlexibleLot();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _lots.Setup(r => r.GetByIdAsync(lot.Id, token)).ReturnsAsync(lot);
        var (exceptions, _) = AddCapabilities();
        exceptions.Setup(r => r.GetRouteExceptionAsync("EX-CANCEL", token))
            .ReturnsAsync((RouteExceptionRequest?)null);
        exceptions.Setup(r => r.TryAddRouteExceptionAsync(
                It.IsAny<RouteExceptionRequest>(), token))
            .ThrowsAsync(new OperationCanceledException(token));

        var act = () => Build().RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            "EX-CANCEL", "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, DateTime.UtcNow.AddHours(1)), token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        exceptions.Verify(r => r.GetRouteExceptionAsync("EX-CANCEL", token), Times.Once,
            "cancellation must not enter the duplicate-key reload path");
    }

    [Fact]
    public async Task Existing_exception_retry_returns_original_before_mutable_lot_lookup()
    {
        var lot = FlexibleLot();
        var (exceptions, _) = AddCapabilities();
        var now = DateTime.UtcNow;
        var existing = RouteExceptionRequest.Request(
            "EX-AFTER-LOT-CHANGE", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "urgent", "operator",
            now, now.AddHours(1), "MOBILE", "PDA-01").Value;
        exceptions.Setup(r => r.GetRouteExceptionAsync(existing.Id, default)).ReturnsAsync(existing);

        var result = await Build().RequestRouteExceptionAsync(new RequestRouteExceptionCommand(
            existing.Id, "P1", lot.Id, RouteDeviationType.Bypass, 2,
            "urgent", "operator", 1, now.AddHours(2), "MOBILE", "PDA-01"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(existing);
        _lots.Verify(r => r.GetByIdAsync(It.IsAny<string>(), default), Times.Never,
            "the ledger result is authoritative for an exact retry after LOT mutation");
    }

    [Fact]
    public async Task Reading_an_expired_request_projects_status_without_writing_the_ledger()
    {
        var lot = FlexibleLot();
        var (exceptions, _) = AddCapabilities();
        var expired = RouteExceptionRequest.Request(
            "EX-EXPIRED", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "old request", "operator",
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1)).Value;
        exceptions.Setup(r => r.GetRouteExceptionAsync(expired.Id, default)).ReturnsAsync(expired);
        var result = await Build().GetRouteExceptionAsync(expired.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(RouteExceptionStatus.Expired);
        exceptions.Verify(r => r.UpdateRouteExceptionAsync(
            It.IsAny<RouteExceptionRequest>(), It.IsAny<RouteExceptionStatus>(), default), Times.Never);
    }

    [Fact]
    public async Task Review_of_expired_request_persists_expired_and_cannot_reject()
    {
        var lot = FlexibleLot();
        var (exceptions, _) = AddCapabilities();
        var expired = RouteExceptionRequest.Request(
            "EX-WRITE-EXPIRED", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "old request", "operator",
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1)).Value;
        exceptions.Setup(r => r.GetRouteExceptionAsync(expired.Id, default)).ReturnsAsync(expired);
        exceptions.Setup(r => r.UpdateRouteExceptionAsync(
                expired, RouteExceptionStatus.Requested, default))
            .ReturnsAsync(true);

        var result = await Build().RejectRouteExceptionAsync(new ReviewRouteExceptionCommand(
            expired.Id, "supervisor", "too late"));

        result.IsFailure.Should().BeTrue();
        expired.Status.Should().Be(RouteExceptionStatus.Expired);
        exceptions.Verify(r => r.UpdateRouteExceptionAsync(
            expired, RouteExceptionStatus.Requested, default), Times.Once);
    }

    [Fact]
    public async Task Approved_review_retry_requires_the_same_review_reason()
    {
        var lot = FlexibleLot();
        var (exceptions, _) = AddCapabilities();
        var now = DateTime.UtcNow;
        var approved = RouteExceptionRequest.Request(
            "EX-REVIEW", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "urgent", "operator",
            now, now.AddHours(1)).Value;
        approved.Approve(
            "supervisor", "capacity checked", now.AddMinutes(1), "MOBILE", "PDA-SUP-01");
        exceptions.Setup(r => r.GetRouteExceptionAsync(approved.Id, default)).ReturnsAsync(approved);

        (await Build().ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            approved.Id, "supervisor", "capacity checked", "MOBILE", "PDA-SUP-01")))
            .IsSuccess.Should().BeTrue();
        (await Build().ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            approved.Id, "supervisor", "different reason", "MOBILE", "PDA-SUP-01")))
            .IsFailure.Should().BeTrue();
        (await Build().ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            approved.Id, "supervisor", "capacity checked", "POP", "PDA-SUP-01")))
            .IsFailure.Should().BeTrue("review channel is part of retry identity");
        (await Build().ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            approved.Id, "supervisor", "capacity checked", "MOBILE", "PDA-SUP-02")))
            .IsFailure.Should().BeTrue("review device is part of retry identity");
    }

    [Fact]
    public async Task Concurrent_same_approval_is_reloaded_as_success()
    {
        var lot = FlexibleLot();
        var (exceptions, _) = AddCapabilities();
        var now = DateTime.UtcNow;
        var requested = RouteExceptionRequest.Request(
            "EX-APPROVE-RACE", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "urgent", "operator",
            now, now.AddHours(1)).Value;
        var concurrent = RouteExceptionRequest.Request(
            requested.Id, lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "urgent", "operator",
            now, now.AddHours(1)).Value;
        concurrent.Approve("supervisor", "checked", now.AddMinutes(1));
        exceptions.SetupSequence(r => r.GetRouteExceptionAsync(requested.Id, default))
            .ReturnsAsync(requested)
            .ReturnsAsync(concurrent);
        exceptions.Setup(r => r.UpdateRouteExceptionAsync(
                requested, RouteExceptionStatus.Requested, default))
            .ReturnsAsync(false);

        var result = await Build().ApproveRouteExceptionAsync(new ReviewRouteExceptionCommand(
            requested.Id, "supervisor", "checked"));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        result.Value.Should().BeSameAs(concurrent);
    }

    [Fact]
    public async Task Flexible_approved_exception_is_consumed_with_lot_and_structured_audit_plan()
    {
        var lot = FlexibleLot();
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, atomic) = AddCapabilities();
        var now = DateTime.UtcNow;
        var exception = RouteExceptionRequest.Request(
            "EX-OK", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 2, "OP10", "OP30", 1, "line bottleneck", "operator",
            now, now.AddHours(1), "MOBILE", "PDA-01").Value;
        exception.Approve("supervisor", "approved", now.AddMinutes(1));
        exceptions.Setup(r => r.GetRouteExceptionAsync(exception.Id, default)).ReturnsAsync(exception);

        LotTransitionPersistPlan? captured = null;
        atomic.Setup(r => r.PersistTransitionAsync(It.IsAny<LotTransitionPersistPlan>(), default))
            .Callback<LotTransitionPersistPlan, CancellationToken>((plan, _) => captured = plan)
            .ReturnsAsync(LotTransitionPersistResult.Persisted);

        var result = await Build().ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "P1", lot.Id, RouteDeviationType.Bypass, 2, "line bottleneck", "operator",
            1, "DEV-1", exception.Id, "MOBILE", "PDA-01"));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        lot.CurrentStepIndex.Should().Be(2);
        exception.Status.Should().Be(RouteExceptionStatus.Applied);
        captured.Should().NotBeNull();
        captured!.RouteException.Should().BeSameAs(exception);
        captured.ExecutionId.Should().Be(exception.AppliedExecutionId);
        captured.RoutingAudit.Should().BeEquivalentTo(new
        {
            FromStepIndex = (int?)0,
            ToStepIndex = (int?)2,
            FromProcessId = "OP10",
            ToProcessId = "OP30",
            ControlMode = RoutingControlMode.Flexible,
            RouteExceptionId = exception.Id,
            ClientChannel = "MOBILE",
            DeviceId = "PDA-01",
            Reason = "line bottleneck"
        });
    }

    [Fact]
    public async Task Alternative_reorders_route_in_atomic_lot_update_plan()
    {
        var lot = Lot.Create(
            "LOT-NC", "P1", null, "ITEM1", 10m,
            ["OP10", "OP20", "OP30", "OP40"], "planner", RoutingControlMode.NoControl).Value;
        lot.TrackIn("EQ", null, null, "operator", DateTime.UtcNow.AddMinutes(-2));
        lot.TrackOut("EQ", 10m, 0m, null, "operator", DateTime.UtcNow.AddMinutes(-1));
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (_, atomic) = AddCapabilities();
        LotTransitionPersistPlan? captured = null;
        atomic.Setup(r => r.PersistTransitionAsync(It.IsAny<LotTransitionPersistPlan>(), default))
            .Callback<LotTransitionPersistPlan, CancellationToken>((plan, _) => captured = plan)
            .ReturnsAsync(LotTransitionPersistResult.Persisted);

        var result = await Build().ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "P1", lot.Id, RouteDeviationType.Alternative, 2, "use available cell", "operator",
            1, "ALT-1"));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        lot.RouteSteps.Should().Equal("OP10", "OP30", "OP20", "OP40");
        lot.CurrentProcessId.Should().Be("OP30");
        lot.NextProcessId.Should().Be("OP20");
        captured!.RoutingAudit.Should().BeEquivalentTo(new
        {
            FromStepIndex = (int?)1,
            ToStepIndex = (int?)1,
            FromProcessId = "OP20",
            ToProcessId = "OP30"
        });
    }

    [Fact]
    public async Task NoControl_bypass_still_blocks_pending_quality_on_any_skipped_process()
    {
        var lot = Lot.Create(
            "LOT-QG", "P1", null, "ITEM1", 10m,
            ["OP10", "OP20", "OP30"], "planner", RoutingControlMode.NoControl).Value;
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (_, atomic) = AddCapabilities();
        _quality.Setup(q => q.EvaluateAsync(lot.Id, "OP10", null, default))
            .ReturnsAsync(ProductionQualityGateResult.NotRequired());
        _quality.Setup(q => q.EvaluateAsync(lot.Id, "OP20", null, default))
            .ReturnsAsync(ProductionQualityGateResult.Pending(1, 0, "SPEC-20"));

        var result = await Build().ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "P1", lot.Id, RouteDeviationType.Bypass, 2, "broken equipment", "operator",
            1, "QG-BYPASS"));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("ROUTE_BYPASS_QUALITY_BLOCKED")
            .And.Contain("OP20").And.Contain("SPEC-20");
        lot.CurrentStepIndex.Should().Be(0);
        atomic.Verify(r => r.PersistTransitionAsync(
            It.IsAny<LotTransitionPersistPlan>(), default), Times.Never);
    }

    [Fact]
    public async Task NoControl_rejects_a_supplied_exception_that_does_not_bind_to_transition()
    {
        var lot = Lot.Create(
            "LOT-NC-BIND", "P1", null, "ITEM1", 10m,
            ["OP10", "OP20", "OP30"], "planner", RoutingControlMode.NoControl).Value;
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (exceptions, atomic) = AddCapabilities();
        var now = DateTime.UtcNow;
        var exception = RouteExceptionRequest.Request(
            "EX-WRONG", lot.Id, lot.PlantId, RouteDeviationType.Bypass,
            0, 1, "OP10", "OP20", 1, "different jump", "operator",
            now, now.AddHours(1)).Value;
        exception.Approve("supervisor", null, now.AddMinutes(1));
        exceptions.Setup(r => r.GetRouteExceptionAsync(exception.Id, default)).ReturnsAsync(exception);

        var result = await Build().ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "P1", lot.Id, RouteDeviationType.Bypass, 2, "actual jump", "operator",
            1, "NC-BIND", exception.Id));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("ROUTE_EXCEPTION_INVALID");
        exception.Status.Should().Be(RouteExceptionStatus.Approved);
        atomic.Verify(r => r.PersistTransitionAsync(
            It.IsAny<LotTransitionPersistPlan>(), default), Times.Never);
    }

    [Fact]
    public void Atomic_repository_is_a_required_constructor_dependency()
    {
        var act = () => new LotTrackingService(
            _lots.Object, null!, _histories.Object, _mixings.Object, _workOrders.Object,
            _master.Object, _quality.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("atomicLots");
    }

    [Fact]
    public async Task Later_guard_concurrency_result_is_returned_as_conflict()
    {
        var lot = Lot.Create(
            "LOT-RACE", "P1", null, "ITEM1", 10m,
            ["OP10", "OP20"], "planner", RoutingControlMode.NoControl).Value;
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (_, atomic) = AddCapabilities();
        atomic.Setup(r => r.PersistTransitionAsync(It.IsAny<LotTransitionPersistPlan>(), default))
            .ReturnsAsync(LotTransitionPersistResult.Conflict);

        var result = await Build().ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "P1", lot.Id, RouteDeviationType.Alternative, 1, "capacity", "operator",
            1, "RACE"));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("changed by another request");
    }

    [Fact]
    public async Task Lot_transition_retry_allows_same_user_and_rejects_cross_user_key_reuse()
    {
        var lot = Lot.Create(
            "LOT-USER", "P1", null, "ITEM1", 10m,
            ["OP10"], "planner", RoutingControlMode.Strict).Value;
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (_, atomic) = AddCapabilities();
        LotTransitionPersistPlan? captured = null;
        atomic.Setup(r => r.PersistTransitionAsync(It.IsAny<LotTransitionPersistPlan>(), default))
            .Callback<LotTransitionPersistPlan, CancellationToken>((plan, _) => captured = plan)
            .ReturnsAsync(LotTransitionPersistResult.Persisted);
        var command = new TrackInCommand(
            "P1", lot.Id, "EQ", null, null, "operator-a",
            1, "USER-BOUND-KEY", "MOBILE", "PDA-01");

        (await Build().TrackInAsync(command)).IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        atomic.Setup(r => r.GetExecutionAsync(command.IdempotencyKey!, default))
            .ReturnsAsync(new LotExecutionRecord(
                lot.Id, LotExecutionId.TrackIn, command.IdempotencyKey!, captured!.RequestHash,
                1, 2));

        (await Build().TrackInAsync(command)).IsSuccess.Should().BeTrue(
            "the authenticated user made an exact network retry");
        var crossUser = await Build().TrackInAsync(command with { User = "operator-b" });
        crossUser.IsFailure.Should().BeTrue();
        crossUser.Error.Description.Should().Contain("idempotency key");
    }

    [Fact]
    public async Task Lot_transition_rejects_idempotency_key_over_database_limit_before_persistence()
    {
        var lot = Lot.Create(
            "LOT-KEY", "P1", null, "ITEM1", 10m,
            ["OP10"], "planner", RoutingControlMode.Strict).Value;
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (_, atomic) = AddCapabilities();

        var result = await Build().TrackInAsync(new TrackInCommand(
            "P1", lot.Id, "EQ", null, null, "operator",
            1, new string('K', 101)));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("100 characters");
        atomic.Verify(r => r.PersistTransitionAsync(
            It.IsAny<LotTransitionPersistPlan>(), default), Times.Never);
    }

    [Fact]
    public async Task Rework_from_running_lot_preserves_source_process_and_equipment_in_history()
    {
        var lot = Lot.Create(
            "LOT-RW", "P1", null, "ITEM1", 10m,
            ["OP10", "OP20", "OP30"], "planner", RoutingControlMode.NoControl).Value;
        lot.TrackIn("EQ10", null, null, "operator", DateTime.UtcNow.AddMinutes(-4));
        lot.TrackOut("EQ10", 10m, 0m, null, "operator", DateTime.UtcNow.AddMinutes(-3));
        lot.TrackIn("EQ20", "RCP20", 2, "operator", DateTime.UtcNow.AddMinutes(-2));
        _lots.Setup(r => r.GetByIdAsync(lot.Id, default)).ReturnsAsync(lot);
        var (_, atomic) = AddCapabilities();
        LotTransitionPersistPlan? captured = null;
        atomic.Setup(r => r.PersistTransitionAsync(It.IsAny<LotTransitionPersistPlan>(), default))
            .Callback<LotTransitionPersistPlan, CancellationToken>((plan, _) => captured = plan)
            .ReturnsAsync(LotTransitionPersistResult.Persisted);

        var result = await Build().ApplyRouteDeviationAsync(new ApplyRouteDeviationCommand(
            "P1", lot.Id, RouteDeviationType.Rework, 0, "inspection failed", "operator",
            1, "RW-1"));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : string.Empty);
        lot.ReturnStepIndex.Should().Be(1);
        lot.EquipmentId.Should().BeNull();
        captured!.Histories.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProcessId = "OP20",
            EquipmentId = "EQ20",
            RecipeDefId = "RCP20",
            ExecutionId = LotExecutionId.Rework,
            Reason = "inspection failed"
        });
    }

}
