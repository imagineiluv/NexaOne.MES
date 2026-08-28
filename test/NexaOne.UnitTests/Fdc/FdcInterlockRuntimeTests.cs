using System.Diagnostics;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcInterlockRuntimeTests
{
    private static readonly IReadOnlyList<FdcInterlockTopology> Topology =
        [new("EQ-001", ["TEMP01", "PRESS01"])];

    [Fact]
    public async Task Startup_fails_closed_when_action_readiness_exceeds_the_bounded_timeout()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<FdcInterlockActionReadiness>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var collector = Collector(
            rules.Object, EmptyHistory().Object, action.Object, TimeSpan.FromMilliseconds(20));

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*readiness check failed*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_fails_closed_when_adapter_does_not_confirm_cancellation_fencing()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcInterlockActionReadiness(
                IsAvailable: true,
                CancellationFencingConfirmed: false,
                Detail: "late release cannot be fenced",
                OutstandingEffects: Array.Empty<FdcInterlockOutstandingEffect>()));
        var collector = Collector(rules.Object, EmptyHistory().Object, action.Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*cancellation/deadline fencing*late release*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_fails_closed_when_adapter_does_not_confirm_shared_output_ownership()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcInterlockActionReadiness(
                IsAvailable: true,
                CancellationFencingConfirmed: true,
                Detail: "shared STOP output is not reference-counted",
                OutstandingEffects: Array.Empty<FdcInterlockOutstandingEffect>())
            {
                AggregateEffectOwnershipConfirmed = false,
            });
        var collector = Collector(rules.Object, EmptyHistory().Object, action.Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*aggregate EffectId ownership*shared STOP output*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Production_runtime_requires_a_bound_durable_authority_before_readiness()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = ReadyAction();
        var collector = Collector(
            rules.Object, EmptyHistory().Object, action.Object, requireRuntimeAuthority: true);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*runtime lease authority*missing or expired*");
        action.Verify(x => x.CheckReadyAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bound_runtime_authority_is_attached_to_every_physical_action_request()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = ReadyAction();
        FdcInterlockActionRequest? applied = null;
        action.Setup(x => x.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockActionRequest, CancellationToken>((request, _) => applied = request)
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("ack"));
        var collector = Collector(
            rules.Object, EmptyHistory().Object, action.Object, requireRuntimeAuthority: true);
        var authority = new FdcRuntimeAuthority(
            "fdc-node-a", 42, new string('a', 64), DateTime.UtcNow.AddSeconds(30));
        collector.BindRuntimeAuthority(authority);

        await InitializeAndPrimeAsync(collector, _ => 90m);

        applied.Should().NotBeNull();
        applied!.RuntimeAuthority.Should().Be(authority);
    }

    [Fact]
    public async Task Apply_result_is_rejected_when_runtime_authority_is_lost_during_the_adapter_call()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = ReadyAction();
        FdcCollectorService? collector = null;
        action.Setup(x => x.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((FdcInterlockActionRequest _, CancellationToken _) =>
            {
                collector!.ClearRuntimeAuthority();
                return Task.FromResult(FdcInterlockActionResult.Confirmed("stale-ack"));
            });
        collector = Collector(
            rules.Object, EmptyHistory().Object, action.Object,
            actionTimeout: TimeSpan.FromSeconds(2), requireRuntimeAuthority: true);
        collector.BindRuntimeAuthority(new FdcRuntimeAuthority(
            "fdc-node-a", 42, new string('a', 64), DateTime.UtcNow.AddMinutes(1)));
        await InitializeAndPrimeAsync(collector);
        var published = false;
        collector.InterlockTriggered += (_, _) => published = true;

        var act = () => collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 90m));

        var failure = await act.Should().ThrowAsync<FdcInterlockActionFailedException>();
        failure.Which.InnerException.Should().BeOfType<FdcInterlockRuntimeUnavailableException>();
        published.Should().BeFalse("a stale acknowledgement must not be accepted or published");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Release_call_is_bounded_by_the_captured_monotonic_lease_deadline()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var history = EmptyHistory([
            OpenEffect("EFFECT-LEASE", "R1", "TEMP01", "STOP"),
        ]);
        history.Setup(x => x.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        history.Setup(x => x.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-ack"));
        var releaseNeverCompletes = new TaskCompletionSource<FdcInterlockReleaseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = false;
        action.Setup(x => x.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .Returns((FdcInterlockReleaseRequest _, CancellationToken token) =>
            {
                token.Register(() => cancellationObserved = true);
                return releaseNeverCompletes.Task;
            });
        var collector = Collector(
            rules.Object, history.Object, action.Object,
            actionTimeout: TimeSpan.FromSeconds(2), requireRuntimeAuthority: true);
        var authority = new FdcRuntimeAuthority(
            "fdc-node-a", 42, new string('a', 64), DateTime.UtcNow.AddMinutes(1));
        collector.BindRuntimeAuthority(authority);
        await InitializeAndPrimeAsync(collector);
        collector.BindRuntimeAuthority(
            authority,
            FdcMonotonicDeadline.FromNow(TimeSpan.FromMilliseconds(50)));
        var elapsed = Stopwatch.StartNew();

        var act = () => EvaluateFreshPollAsync(
            collector, tempValue: 20m, pressureValue: 20m);

        await act.Should().ThrowAsync<FdcInterlockActionFailedException>()
            .WithMessage("*release*timed out*unknown physical outcome*");
        elapsed.Stop();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "the two-second adapter timeout must be clamped to the captured lease remainder");
        cancellationObserved.Should().BeTrue();
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Expired_local_authority_deadline_synchronously_revokes_permit_and_rejects_the_sample_path()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("should-not-run"));
        var collector = Collector(
            rules.Object, EmptyHistory().Object, action.Object, requireRuntimeAuthority: true);
        var authority = new FdcRuntimeAuthority(
            "fdc-node-a", 42, new string('a', 64), DateTime.UtcNow.AddMinutes(1));
        collector.BindRuntimeAuthority(authority);
        await InitializeAndPrimeAsync(collector);
        collector.IsRunPermitted.Should().BeTrue();

        collector.BindRuntimeAuthority(
            authority,
            FdcMonotonicDeadline.FromNow(TimeSpan.FromMilliseconds(50)));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        collector.IsRunPermitted.Should().BeFalse(
            "permit reads must fence synchronously even if the lease-renew continuation was suspended");
        var sample = () => collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 90m));
        await sample.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>();
        action.Verify(x => x.ApplyAsync(
            It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Monotonic_lease_deadline_is_anchored_to_remote_call_start_instead_of_response_time()
    {
        var operationStarted = Stopwatch.GetTimestamp();
        var configuredTtl = TimeSpan.FromSeconds(3);

        var deadline = FdcMonotonicDeadline.FromOperationStart(operationStarted, configuredTtl);
        var simulatedResponseTimestamp = operationStarted + Stopwatch.Frequency * 2;

        Stopwatch.GetElapsedTime(operationStarted, deadline)
            .Should().BeCloseTo(configuredTtl, TimeSpan.FromMilliseconds(1));
        Stopwatch.GetElapsedTime(simulatedResponseTimestamp, deadline)
            .Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
                "two seconds of DB latency must consume two seconds of local authority");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_local_authority_blocks_startup_snapshot_and_persistence_retry_writer_paths(
        bool persistenceRetryPath)
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var collector = Collector(
            rules.Object, EmptyHistory().Object, ReadyAction().Object, requireRuntimeAuthority: true);
        var authority = new FdcRuntimeAuthority(
            "fdc-node-a", 42, new string('a', 64), DateTime.UtcNow.AddMinutes(1));
        collector.BindRuntimeAuthority(authority);
        await collector.InitializeInterlockRuntimeAsync(Topology);
        if (persistenceRetryPath)
        {
            await collector.EvaluateInitialSnapshotAsync(
                "EQ-001", [Sample("TEMP01", 20m), Sample("PRESS01", 20m)]);
            collector.CompleteInterlockRuntimeInitialization();
        }

        collector.BindRuntimeAuthority(
            authority,
            FdcMonotonicDeadline.FromNow(TimeSpan.FromMilliseconds(30)));
        await Task.Delay(TimeSpan.FromMilliseconds(60));

        Func<Task> writerPath = persistenceRetryPath
            ? () => collector.RetryPendingEffectPersistenceAsync()
            : () => collector.EvaluateInitialSnapshotAsync(
                "EQ-001", [Sample("TEMP01", 20m), Sample("PRESS01", 20m)]);

        await writerPath.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*runtime lease authority*monotonic deadline*action publication is fenced*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public void Action_results_require_a_nonblank_acknowledgement_id()
    {
        new FdcInterlockActionResult(true, true, " ", null).IsConfirmed.Should().BeFalse();
        new FdcInterlockReleaseResult(true, true, false, null, null).IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public void Action_port_records_preserve_their_original_positional_abi()
    {
        AssertPositionalAbi<FdcInterlockActionRequest>(9);
        AssertPositionalAbi<FdcInterlockActionRequest>(10);
        AssertPositionalAbi<FdcInterlockActionReadiness>(4);
        AssertPositionalAbi<FdcInterlockActionReadiness>(6);
        AssertPositionalAbi<FdcInterlockReleaseRequest>(8);
        AssertPositionalAbi<FdcInterlockReleaseRequest>(9);

        AssertOptionalExtensionAbi<FdcInterlockActionRequest>(
            10,
            ("RuntimeAuthority", null));
        AssertOptionalExtensionAbi<FdcInterlockActionReadiness>(
            6,
            ("AggregateEffectOwnershipConfirmed", false),
            ("RuntimeFencePersistenceConfirmed", false));
        AssertOptionalExtensionAbi<FdcInterlockReleaseRequest>(
            9,
            ("RuntimeAuthority", null));
    }

    [Fact]
    public void Legacy_ready_is_fail_closed_until_both_controller_evidence_flags_are_explicitly_confirmed()
    {
        var legacy = FdcInterlockActionReadiness.Ready();
        var attested = FdcInterlockActionReadiness.ReadyWithEvidence(
            aggregateEffectOwnershipConfirmed: true,
            runtimeFencePersistenceConfirmed: true);

        legacy.AggregateEffectOwnershipConfirmed.Should().BeFalse();
        legacy.RuntimeFencePersistenceConfirmed.Should().BeFalse();
        attested.AggregateEffectOwnershipConfirmed.Should().BeTrue();
        attested.RuntimeFencePersistenceConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task Startup_fails_closed_when_recovery_action_exceeds_the_bounded_timeout()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R1", "TEMP01", "STOP")]);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<FdcInterlockActionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var collector = Collector(
            rules.Object,
            EmptyHistory([OpenEffect("E1", "R1", "TEMP01", "STOP")]).Object,
            action.Object,
            TimeSpan.FromMilliseconds(20));

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockActionFailedException>()
            .WithMessage("*before acknowledgement/readback*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_does_not_grant_run_permit_until_every_active_parameter_has_an_initial_snapshot()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var collector = Collector(rules.Object, EmptyHistory().Object, ReadyAction().Object);

        await collector.InitializeInterlockRuntimeAsync(Topology);

        collector.IsRunPermitted.Should().BeFalse(
            "rule/action readiness does not prove the live PLC values are safe");
        await collector.EvaluateInitialSnapshotAsync(
            "EQ-001",
            [Sample("TEMP01", 20m)]);
        var incomplete = () => collector.CompleteInterlockRuntimeInitialization();
        incomplete.Should().Throw<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*initial snapshot*PRESS01*");

        await collector.EvaluateInitialSnapshotAsync(
            "EQ-001",
            [Sample("PRESS01", 20m)]);
        collector.CompleteInterlockRuntimeInitialization();

        collector.IsRunPermitted.Should().BeTrue();
    }

    [Fact]
    public async Task Bad_quality_on_an_interlock_input_revokes_permit_before_recording_telemetry()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var parameters = new Mock<IFdcParameterRepository>();
        parameters.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string parameterId, CancellationToken _) =>
                FdcParameter.Create(parameterId, parameterId, "EQ-001", "unit", 0m, 100m).Value);
        var telemetrySawRevokedPermit = false;
        FdcCollectorService? collector = null;
        var collect = new Mock<IFdcCollectDataRepository>();
        collect.Setup(x => x.AddAsync(
                It.Is<FdcCollectData>(row => row.ParameterId == "TEMP01" && row.Quality == "Bad"),
                It.IsAny<CancellationToken>()))
            .Callback(() => telemetrySawRevokedPermit = collector!.IsRunPermitted is false)
            .Returns(Task.CompletedTask);
        var action = ReadyAction();
        collector = new FdcCollectorService(
            new FdcDataService(parameters.Object, collect.Object),
            new FdcInterlockService(rules.Object, EmptyHistory().Object),
            actionPort: action.Object);
        await collector.InitializeInterlockRuntimeAsync(Topology);
        await collector.EvaluateInitialSnapshotAsync(
            "EQ-001",
            [Sample("TEMP01", 20m), Sample("PRESS01", 20m)]);
        collector.CompleteInterlockRuntimeInitialization();

        var act = () => collector.OnTagChangeAsync(
            "EQ-001",
            new FdcTagSample("TEMP01", 0m, FdcSampleQuality.Bad));

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*TEMP01*quality*run permit*");
        collector.IsRunPermitted.Should().BeFalse();
        telemetrySawRevokedPermit.Should().BeTrue(
            "telemetry persistence must follow the fail-closed interlock decision");
        action.Verify(x => x.ApplyAsync(
            It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "a fallback zero must not be treated as a real process value or invent a quality-loss action");
    }

    [Fact]
    public async Task Initialized_runtime_uses_preloaded_rules_and_open_effects_on_the_sample_path()
    {
        var rule = Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE");
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var history = EmptyHistory();
        history.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("ack-1"));
        var collector = Collector(rules.Object, history.Object, action.Object);

        await InitializeAndPrimeAsync(collector);
        await collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 90m));
        await collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 95m));

        collector.IsRunPermitted.Should().BeFalse(
            "an active physical interlock effect must hold automatic-run admission while monitoring stays alive");
        rules.Verify(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()), Times.Once);
        rules.Verify(x => x.GetActiveRulesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the sample path must evaluate the immutable startup snapshot");
        history.Verify(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()), Times.Once);
        history.Verify(x => x.GetUnresolvedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "open effects must be recovered in one global startup inventory read");
        action.Verify(x => x.ApplyAsync(
            It.Is<FdcInterlockActionRequest>(request =>
                request.EffectId.Length == 32 &&
                request.Action == "STOP.TEMPERATURE" &&
                !request.IsRecovery),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Startup_reconciles_every_open_effect_with_the_original_effect_id()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE"),
                Rule("R-PRESS", "PRESS01", "STOP.PRESSURE")
            ]);
        var openTemperature = OpenEffect("EFFECT-TEMP", "R-TEMP", "TEMP01", "STOP.TEMPERATURE");
        var openPressure = OpenEffect("EFFECT-PRESS", "R-PRESS", "PRESS01", "STOP.PRESSURE");
        var history = EmptyHistory([openTemperature, openPressure]);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockActionRequest request, CancellationToken _) =>
                FdcInterlockActionResult.Confirmed($"ack:{request.EffectId}"));
        var collector = Collector(rules.Object, history.Object, action.Object);

        await InitializeAndPrimeAsync(collector);
        await EvaluateFreshPollAsync(collector, tempValue: 20m, pressureValue: 20m);

        action.Verify(x => x.ApplyAsync(
            It.Is<FdcInterlockActionRequest>(request =>
                request.EffectId == "EFFECT-TEMP" && request.IsRecovery),
            It.IsAny<CancellationToken>()), Times.Once);
        action.Verify(x => x.ApplyAsync(
            It.Is<FdcInterlockActionRequest>(request =>
                request.EffectId == "EFFECT-PRESS" && request.IsRecovery),
            It.IsAny<CancellationToken>()), Times.Once);
        collector.IsRunPermitted.Should().BeTrue();
    }

    [Fact]
    public async Task Startup_reasserts_a_stale_normalized_effect_and_does_not_release_when_the_current_snapshot_still_violates()
    {
        var rule = Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE");
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var triggeredAt = DateTime.UtcNow.AddMinutes(-10);
        var open = FdcInterlockHistory.Create(
            "EFFECT-STALE", rule.Id, "EQ-001", "TEMP01", 90m,
            rule.Action, "stale normalization", triggeredAt).Value;
        open.MarkApplied("old-apply", triggeredAt.AddSeconds(1));
        open.MarkConditionNormalized(triggeredAt.AddMinutes(1), 50m);
        var history = EmptyHistory([open]);
        history.Setup(x => x.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var reconciled = new List<string>();
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("unused"));
        action.Setup(x => x.ReconcileAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockActionRequest, CancellationToken>((request, _) => reconciled.Add(request.EffectId))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("reasserted"));
        var collector = Collector(rules.Object, history.Object, action.Object);

        await collector.InitializeInterlockRuntimeAsync(Topology);
        action.Verify(x => x.ReleaseAsync(
            It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "persisted normalization cannot unlock before a current PLC snapshot");

        await collector.EvaluateInitialSnapshotAsync("EQ-001", [Sample("TEMP01", 95m)]);

        reconciled.Should().Equal("EFFECT-STALE");
        action.Verify(x => x.ReleaseAsync(
            It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "the current violating value must keep the reasserted stop active");
    }

    [Fact]
    public async Task Startup_releases_a_reasserted_effect_only_after_a_current_completed_poll()
    {
        var rule = Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE");
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var triggeredAt = DateTime.UtcNow.AddMinutes(-10);
        var open = FdcInterlockHistory.Create(
            "EFFECT-NORMAL", rule.Id, "EQ-001", "TEMP01", 90m,
            rule.Action, "old normalization", triggeredAt).Value;
        open.MarkApplied("old-apply", triggeredAt.AddSeconds(1));
        open.MarkConditionNormalized(triggeredAt.AddMinutes(1), 50m);
        var history = EmptyHistory([open]);
        history.Setup(x => x.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var order = new List<string>();
        var action = ReadyAction();
        action.Setup(x => x.ReconcileAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("reconcile"))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("reasserted"));
        action.Setup(x => x.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("release"))
            .ReturnsAsync(FdcInterlockReleaseResult.Confirmed("released"));
        var collector = Collector(rules.Object, history.Object, action.Object);

        await collector.InitializeInterlockRuntimeAsync(Topology);
        order.Should().Equal("reconcile");

        await collector.EvaluateInitialSnapshotAsync("EQ-001", [Sample("TEMP01", 50m)]);
        await collector.EvaluateInitialSnapshotAsync("EQ-001", [Sample("PRESS01", 20m)]);
        collector.CompleteInterlockRuntimeInitialization();
        order.Should().Equal(["reconcile"],
            "startup baseline may record normalization but must not physically release");

        await EvaluateFreshPollAsync(collector, tempValue: 50m, pressureValue: 20m);

        order.Should().Equal("reconcile", "release");
        open.IsResolved.Should().BeTrue();
        open.ReleaseConfirmedAt.Should().NotBeNull();
        open.ResolvedAt.Should().Be(open.ReleaseConfirmedAt);
    }

    [Fact]
    public async Task Startup_imports_and_reconciles_an_adapter_only_durable_effect_with_the_same_effect_id()
    {
        var rule = Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE");
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        FdcInterlockHistory? durable = null;
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetByIdAsync("ADAPTER-ONLY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable);
        history.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, CancellationToken>((effect, _) => durable = effect)
            .Returns(Task.CompletedTask);
        history.Setup(x => x.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var triggeredAt = DateTime.UtcNow.AddSeconds(-10);
        var inventory = new FdcInterlockOutstandingEffect(
            new FdcInterlockActionRequest(
                "ADAPTER-ONLY", rule.Id, "EQ-001", "TEMP01", 91m,
                rule.Action, false, triggeredAt, "adapter durable journal"),
            "adapter-ack",
            triggeredAt.AddSeconds(1));
        var action = ReadyAction();
        action.Setup(x => x.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true,
                [inventory]));
        action.Setup(x => x.ReconcileAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("reconciled"));
        var collector = Collector(rules.Object, history.Object, action.Object);

        await collector.InitializeInterlockRuntimeAsync(Topology);

        durable.Should().NotBeNull();
        durable!.Id.Should().Be("ADAPTER-ONLY");
        durable.EffectState.Should().Be(FdcInterlockEffectState.Applied);
        action.Verify(x => x.ReconcileAsync(
            It.Is<FdcInterlockActionRequest>(request => request.EffectId == "ADAPTER-ONLY"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Startup_fails_closed_when_the_project_action_adapter_is_unavailable()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.Unavailable("project action adapter is not configured"));
        var collector = Collector(rules.Object, EmptyHistory().Object, action.Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*project action adapter is not configured*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Trigger_fails_closed_until_acknowledgement_and_readback_are_both_confirmed()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var history = EmptyHistory();
        history.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdcInterlockActionResult(
                Acknowledged: true,
                ReadbackConfirmed: false,
                AcknowledgementId: "ack-without-readback",
                Detail: "output readback remained false"));
        var collector = Collector(rules.Object, history.Object, action.Object);
        var notified = false;
        collector.InterlockTriggered += (_, _) => notified = true;
        await InitializeAndPrimeAsync(collector);

        var act = () => collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 90m));

        await act.Should().ThrowAsync<FdcInterlockActionFailedException>()
            .WithMessage("*readback*");
        collector.IsRunPermitted.Should().BeFalse();
        notified.Should().BeFalse("an effect is not applied until both acknowledgement and readback succeed");
        history.Verify(x => x.AddAsync(
            It.Is<FdcInterlockHistory>(effect => effect.Action == "STOP.TEMPERATURE"),
            It.IsAny<CancellationToken>()), Times.Once,
            "detection evidence must survive an action failure");
    }

    [Fact]
    public async Task Trigger_awaits_action_readback_before_notification_and_collect_database_write()
    {
        var rule = Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE");
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var history = EmptyHistory();
        history.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var actionResult = new TaskCompletionSource<FdcInterlockActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(actionResult.Task);

        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(x => x.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var collectRepository = new Mock<IFdcCollectDataRepository>();
        collectRepository.Setup(x => x.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, collectRepository.Object),
            new FdcInterlockService(rules.Object, history.Object),
            actionPort: action.Object);
        await InitializeAndPrimeAsync(collector);
        collectRepository.Invocations.Clear();
        var notified = false;
        collector.InterlockTriggered += (_, _) => notified = true;

        var processing = collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 90m));
        await Task.Yield();

        processing.IsCompleted.Should().BeFalse("the sample callback must await project action acknowledgement/readback");
        notified.Should().BeFalse();
        collectRepository.Verify(x => x.AddAsync(
            It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()), Times.Never,
            "telemetry persistence cannot delay the interlock action");

        actionResult.SetResult(FdcInterlockActionResult.Confirmed("ack-after-readback"));
        await processing;

        notified.Should().BeTrue();
        collectRepository.Verify(x => x.AddAsync(
            It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Startup_fails_closed_when_an_equipment_has_no_active_rules()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockRule>());
        var collector = Collector(rules.Object, EmptyHistory().Object, ReadyAction().Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EQ-001*active interlock rule*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_fails_closed_when_an_open_effect_is_outside_the_observable_topology()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var orphan = OpenEffect("EFFECT-ORPHAN", "R-OLD", "REMOVED01", "STOP.LEGACY");
        var collector = Collector(rules.Object, EmptyHistory([orphan]).Object, ReadyAction().Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EFFECT-ORPHAN*REMOVED01*outside the loaded topology*");
        collector.IsRunPermitted.Should().BeFalse(
            "an effect that cannot receive a normal sample can never be safely reconciled or resolved");
    }

    [Fact]
    public async Task Startup_fails_closed_when_global_open_effect_belongs_to_removed_equipment()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var orphan = FdcInterlockHistory.Create(
            "EFFECT-REMOVED-EQ", "R-OLD", "EQ-REMOVED", "TEMP01",
            90m, "STOP.LEGACY", "open", DateTime.UtcNow).Value;
        var collector = Collector(rules.Object, EmptyHistory([orphan]).Object, ReadyAction().Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EFFECT-REMOVED-EQ*EQ-REMOVED/TEMP01*outside the loaded topology*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_fails_closed_when_an_open_effect_no_longer_matches_the_active_rule_action()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.NEW")]);
        var stale = OpenEffect("EFFECT-OLD-ACTION", "R-TEMP", "TEMP01", "STOP.OLD");
        var collector = Collector(rules.Object, EmptyHistory([stale]).Object, ReadyAction().Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EFFECT-OLD-ACTION*no longer matches active rule/action*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_rejects_an_invalid_persisted_active_rule_instead_of_silently_ignoring_it()
    {
        var invalid = FdcInterlockRule.Restore(
            "R-BAD", "Bad operator", "EQ-001", "TEMP01", "GTT",
            80m, "STOP", 1, isActive: true);
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([invalid]);
        var collector = Collector(rules.Object, EmptyHistory().Object, ReadyAction().Object);

        var act = () => collector.InitializeInterlockRuntimeAsync(Topology);

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*R-BAD*invalid*Operator*");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Rule_mutation_is_rejected_while_runtime_is_active()
    {
        var persisted = new List<FdcInterlockRule>
        {
            Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")
        };
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted.ToArray());
        rules.Setup(x => x.AddAsync(It.IsAny<FdcInterlockRule>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockRule, CancellationToken>((rule, _) => persisted.Add(rule))
            .Returns(Task.CompletedTask);
        var history = EmptyHistory();
        var action = ReadyAction();
        var interlock = new FdcInterlockService(rules.Object, history.Object);
        var parameterRepository = new Mock<IFdcParameterRepository>();
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            interlock,
            actionPort: action.Object);
        await InitializeAndPrimeAsync(collector);
        collector.IsRunPermitted.Should().BeTrue();

        var created = await interlock.CreateRuleAsync(
            "R-PRESS", "Pressure", "EQ-001", "PRESS01", "GT", 80m, "STOP.PRESSURE", 1);

        created.IsFailure.Should().BeTrue();
        created.Error.Type.Should().Be(NexaOne.Common.ErrorType.Conflict);
        collector.IsRunPermitted.Should().BeTrue(
            "a rejected maintenance mutation cannot diverge the persisted rules from the active snapshot");
        rules.Verify(x => x.AddAsync(
            It.IsAny<FdcInterlockRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Simultaneous_matching_rules_execute_each_action_and_resolve_only_the_cleared_rule()
    {
        var stop = FdcInterlockRule.Create(
            "R-STOP", "Stop", "EQ-001", "TEMP01", "GT", 90m, "STOP", 1).Value;
        var warning = FdcInterlockRule.Create(
            "R-WARN", "Warning", "EQ-001", "TEMP01", "GT", 70m, "WARN", 10).Value;
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([warning, stop]);

        var durable = new Dictionary<string, FdcInterlockHistory>(StringComparer.Ordinal);
        var resolvedRuleIds = new List<string>();
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetUnresolvedAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string effectId, CancellationToken _) =>
                durable.TryGetValue(effectId, out var value) ? value : null);
        history.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, CancellationToken>((effect, _) => durable.Add(effect.Id, effect))
            .Returns(Task.CompletedTask);
        history.Setup(x => x.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, int, CancellationToken>((effect, _, _) =>
            {
                if (effect.IsResolved) resolvedRuleIds.Add(effect.RuleId);
            })
            .ReturnsAsync(true);

        var actions = new List<string>();
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockActionRequest, CancellationToken>((request, _) => actions.Add(request.Action))
            .ReturnsAsync((FdcInterlockActionRequest request, CancellationToken _) =>
                FdcInterlockActionResult.Confirmed($"ack:{request.EffectId}"));
        var collector = Collector(rules.Object, history.Object, action.Object);
        await InitializeAndPrimeAsync(collector);

        await collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 95m));
        await collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 80m));
        await EvaluateFreshPollAsync(collector, tempValue: 80m, pressureValue: 20m);

        actions.Should().Equal(new[] { "STOP", "WARN" },
            "a lower-priority warning must not mask a simultaneously matching stop action");
        durable.Values.Should().HaveCount(2);
        resolvedRuleIds.Should().Equal("R-STOP");
        durable.Values.Single(effect => effect.RuleId == "R-WARN").IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_preloads_all_open_alarms_without_a_first_sample_point_read_or_duplicate()
    {
        var rules = new Mock<IFdcInterlockRuleRepository>();
        rules.Setup(x => x.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rule("R-TEMP", "TEMP01", "STOP.TEMPERATURE")]);
        var interlockHistory = EmptyHistory();
        interlockHistory.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var alarmConfig = FdcAlarmConfig.Create(
            "A-TEMP", "EQ-001", "TEMP01", "Critical", "GT", 80m).Value;
        var alarmConfigs = new Mock<IFdcAlarmConfigRepository>();
        alarmConfigs.Setup(x => x.GetActiveConfigsAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync([alarmConfig]);
        var durableAlarm = FdcAlarmHistory.Create(
            "ALARM-OPEN", "A-TEMP", "EQ-001", "TEMP01", "Critical", 90m,
            "open", DateTime.UtcNow).Value;
        var alarmHistory = new Mock<IFdcAlarmHistoryRepository>();
        alarmHistory.Setup(x => x.GetOpenAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([durableAlarm]);

        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(x => x.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var dataRepository = new Mock<IFdcCollectDataRepository>();
        dataRepository.Setup(x => x.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var action = ReadyAction();
        action.Setup(x => x.ApplyAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("ack"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, dataRepository.Object),
            new FdcInterlockService(rules.Object, interlockHistory.Object),
            new FdcAlarmService(alarmConfigs.Object, alarmHistory.Object),
            action.Object);

        await InitializeAndPrimeAsync(collector, _ => 90m);
        await collector.OnTagChangeAsync("EQ-001", Sample("TEMP01", 90m));

        alarmHistory.Verify(x => x.GetOpenAsync("EQ-001", It.IsAny<CancellationToken>()), Times.Once);
        alarmHistory.Verify(x => x.GetOpenAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the startup equipment read owns open-state recovery");
        alarmHistory.Verify(x => x.AddAsync(
            It.IsAny<FdcAlarmHistory>(), It.IsAny<CancellationToken>()), Times.Never,
            "a recovered open alarm suppresses duplicate history on the first violating sample");
    }

    private static FdcCollectorService Collector(
        IFdcInterlockRuleRepository rules,
        IFdcInterlockHistoryRepository history,
        IFdcInterlockActionPort action,
        TimeSpan? actionTimeout = null,
        bool requireRuntimeAuthority = false)
    {
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string parameterId, CancellationToken _) =>
                FdcParameter.Create(parameterId, parameterId, "EQ-001", "unit", 0m, 100m).Value);
        var dataRepository = new Mock<IFdcCollectDataRepository>();
        dataRepository.Setup(x => x.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, dataRepository.Object),
            new FdcInterlockService(rules, history),
            actionPort: action,
            actionTimeout: actionTimeout,
            requireRuntimeAuthority: requireRuntimeAuthority);
    }

    private static async Task InitializeAndPrimeAsync(
        FdcCollectorService collector,
        Func<string, decimal>? value = null)
    {
        await collector.InitializeInterlockRuntimeAsync(Topology);
        foreach (var equipment in Topology)
        {
            await collector.EvaluateInitialSnapshotAsync(
                equipment.EquipmentId,
                equipment.ParameterIds.Select(parameterId =>
                    Sample(parameterId, value?.Invoke(parameterId) ?? 20m)).ToArray());
        }
        collector.CompleteInterlockRuntimeInitialization();
    }

    private static void AssertPositionalAbi<T>(int parameterCount)
    {
        typeof(T).GetConstructors()
            .Should().ContainSingle(constructor => constructor.GetParameters().Length == parameterCount);
        typeof(T).GetMethods()
            .Where(method => string.Equals(method.Name, "Deconstruct", StringComparison.Ordinal))
            .Should().ContainSingle(method => method.GetParameters().Length == parameterCount);
    }

    private static void AssertOptionalExtensionAbi<T>(
        int parameterCount,
        params (string Name, object? DefaultValue)[] optionalParameters)
    {
        var parameters = typeof(T).GetConstructors()
            .Single(constructor => constructor.GetParameters().Length == parameterCount)
            .GetParameters();
        foreach (var expected in optionalParameters)
        {
            var parameter = parameters.Single(candidate => candidate.Name == expected.Name);
            parameter.HasDefaultValue.Should().BeTrue();
            parameter.DefaultValue.Should().Be(expected.DefaultValue);
        }
    }

    private static Task<bool> EvaluateFreshPollAsync(
        FdcCollectorService collector,
        decimal tempValue,
        decimal pressureValue) =>
        collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Sample("TEMP01", tempValue), Sample("PRESS01", pressureValue)],
            isSnapshotCurrent: static () => true);

    private static Mock<IFdcInterlockHistoryRepository> EmptyHistory(
        IReadOnlyList<FdcInterlockHistory>? open = null)
    {
        var history = new Mock<IFdcInterlockHistoryRepository>();
        history.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(open ?? Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetUnresolvedAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(open ?? Array.Empty<FdcInterlockHistory>());
        history.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string effectId, CancellationToken _) => open?.SingleOrDefault(x => x.Id == effectId));
        return history;
    }

    private static Mock<IFdcInterlockActionPort> ReadyAction()
    {
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(x => x.CheckReadyAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(x => x.ReconcileAsync(It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((FdcInterlockActionRequest request, CancellationToken token) =>
                action.Object.ApplyAsync(request, token));
        action.Setup(x => x.ReleaseAsync(It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockReleaseRequest request, CancellationToken _) =>
                FdcInterlockReleaseResult.Confirmed($"release:{request.EffectId}"));
        return action;
    }

    private static FdcInterlockRule Rule(string id, string parameterId, string action) =>
        FdcInterlockRule.Create(
            id, id, "EQ-001", parameterId, "GT", 80m, action, 1).Value;

    private static FdcInterlockHistory OpenEffect(
        string effectId,
        string ruleId,
        string parameterId,
        string action) =>
        FdcInterlockHistory.Create(
            effectId, ruleId, "EQ-001", parameterId, 90m, action, "open", DateTime.UtcNow).Value;

    private static FdcTagSample Sample(string parameterId, decimal value) =>
        new(parameterId, value, FdcSampleQuality.Good);
}
