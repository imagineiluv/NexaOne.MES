using NexaOne.ServiceContracts.Fdc;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Fdc;

/// <summary>§10.4 — PLC 태그 변경 이벤트(NexaLogic PlcTagChangeEvent)를 FDC 수집 데이터로
/// 적재하는 오케스트레이터의 품질/값 변환과 저장 연결을 검증한다.</summary>
public sealed class FdcCollectorServiceTests
{
    private static readonly IReadOnlyList<FdcInterlockTopology> InterlockTopology =
        [new("EQ-001", ["TEMP01"])];

    private static FdcTagSample Event(string tag, object? after, PlcQuality quality) =>
        PlcDeviceInterface.NormalizeSample(new PlcTagChangeEvent(
            "test-event", "test-endpoint", tag, "test-address", null, after,
            quality, DateTimeOffset.UnixEpoch, "test", true));

    [Fact]
    public void Runtime_key_preserves_equipment_and_parameter_boundaries()
    {
        var first = new FdcRuntimeKey("EQ\u001fZONE", "TEMP");
        var second = new FdcRuntimeKey("EQ", "ZONE\u001fTEMP");

        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData(PlcQuality.Good, FdcSampleQuality.Good)]
    [InlineData(PlcQuality.Uncertain, FdcSampleQuality.Uncertain)]
    [InlineData(PlcQuality.Bad, FdcSampleQuality.Bad)]
    [InlineData(PlcQuality.Timeout, FdcSampleQuality.Bad)]
    [InlineData(PlcQuality.Disconnected, FdcSampleQuality.Bad)]
    [InlineData(PlcQuality.NotSupported, FdcSampleQuality.Bad)]
    public void MapQuality_maps_plc_quality_to_fdc_quality(PlcQuality input, FdcSampleQuality expected)
        => PlcDeviceInterface.MapQuality(input).Should().Be(expected);

    [Fact]
    public void NormalizeSample_preserves_valid_numeric_values()
    {
        Event("TEMP01", 42, PlcQuality.Good).Should().BeEquivalentTo(
            new FdcTagSample("TEMP01", 42m, FdcSampleQuality.Good));
        Event("TEMP01", 55.5, PlcQuality.Good).Should().BeEquivalentTo(
            new FdcTagSample("TEMP01", 55.5m, FdcSampleQuality.Good));
        Event("TEMP01", "12.25", PlcQuality.Good).Should().BeEquivalentTo(
            new FdcTagSample("TEMP01", 12.25m, FdcSampleQuality.Good));
    }

    [Fact]
    public async Task OnTagChange_records_collect_data_with_converted_value_and_quality()
    {
        var param = FdcParameter.Create("TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>())).ReturnsAsync(param);

        FdcCollectData? saved = null;
        var dataRepo = new Mock<IFdcCollectDataRepository>();
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
                .Callback<FdcCollectData, CancellationToken>((d, _) => saved = d)
                .Returns(Task.CompletedTask);

        var sut = new FdcCollectorService(new FdcDataService(paramRepo.Object, dataRepo.Object));

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 55.5, PlcQuality.Good));

        saved.Should().NotBeNull("정의된 파라미터의 태그 변경은 수집 데이터로 적재된다");
        saved!.EquipmentId.Should().Be("EQ-001");
        saved.ParameterId.Should().Be("TEMP01");
        saved.Value.Should().Be(55.5m);
        saved.Quality.Should().Be("Good");
    }

    [Fact]
    public async Task OnTagChange_swallows_unknown_parameter_without_recording()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((FdcParameter?)null);
        var dataRepo = new Mock<IFdcCollectDataRepository>();

        var sut = new FdcCollectorService(new FdcDataService(paramRepo.Object, dataRepo.Object));

        var act = () => sut.OnTagChangeAsync("EQ-001", Event("UNKNOWN", 1, PlcQuality.Good));

        await act.Should().NotThrowAsync("미정의 파라미터는 수집 루프를 막지 않는다");
        dataRepo.Verify(r => r.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 인터락 평가 연결 (§10.4.2) ──────────────────────────────────────────────

    private static (FdcCollectorService sut, Mock<IFdcInterlockRuleRepository> ruleRepo)
        BuildWithInterlock(decimal lower = 0m, decimal upper = 100m)
    {
        var param = FdcParameter.Create("TEMP01", "Temperature", "EQ-001", "C", lower, upper).Value;
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>())).ReturnsAsync(param);
        var dataRepo = new Mock<IFdcCollectDataRepository>();

        var ruleRepo = new Mock<IFdcInterlockRuleRepository>();
        var dataService = new FdcDataService(paramRepo.Object, dataRepo.Object);
        var historyRepo = EmptyInterlockHistory();
        var interlock = new FdcInterlockService(ruleRepo.Object, historyRepo.Object);
        return (new FdcCollectorService(
            dataService, interlock, actionPort: new ConfirmedInterlockActionPort()), ruleRepo);
    }

    [Fact]
    public async Task OnTagChange_raises_event_when_interlock_rule_triggers()
    {
        var (sut, ruleRepo) = BuildWithInterlock();
        var rule = FdcInterlockRule.Create("R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        ruleRepo.Setup(r => r.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { rule });
        await InitializeInterlockAsync(sut);

        FdcInterlockTriggeredEventArgs? fired = null;
        sut.InterlockTriggered += (_, e) => fired = e;

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));

        fired.Should().NotBeNull("임계치 초과 시 인터락 이벤트가 발생한다");
        fired!.EquipmentId.Should().Be("EQ-001");
        fired.ParameterId.Should().Be("TEMP01");
        fired.Value.Should().Be(90m);
        fired.Result.IsTriggered.Should().BeTrue();
        fired.Result.Action.Should().Be("STOP");
    }

    [Fact]
    public async Task OnTagChange_does_not_raise_event_when_rule_passes()
    {
        var (sut, ruleRepo) = BuildWithInterlock();
        var rule = FdcInterlockRule.Create("R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        ruleRepo.Setup(r => r.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { rule });
        await InitializeInterlockAsync(sut);

        var fired = false;
        sut.InterlockTriggered += (_, _) => fired = true;

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));

        fired.Should().BeFalse("임계치 이내면 인터락 이벤트가 발생하지 않는다");
    }

    private static void SetupRule((FdcCollectorService sut, Mock<IFdcInterlockRuleRepository> ruleRepo) t)
    {
        var rule = FdcInterlockRule.Create("R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        t.ruleRepo.Setup(r => r.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { rule });
    }

    [Fact]
    public async Task OnTagChange_triggers_once_for_repeated_violations()
    {
        var t = BuildWithInterlock();
        SetupRule(t);
        await InitializeInterlockAsync(t.sut);
        var triggers = 0;
        t.sut.InterlockTriggered += (_, _) => triggers++;

        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));
        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 95.0, PlcQuality.Good));

        triggers.Should().Be(1, "발동 중에는 중복 통지하지 않는다");
    }

    [Fact]
    public async Task OnTagChange_resolves_when_value_returns_to_normal()
    {
        var t = BuildWithInterlock();
        SetupRule(t);
        await InitializeInterlockAsync(t.sut);
        FdcInterlockResolvedEventArgs? resolved = null;
        t.sut.InterlockResolved += (_, e) => resolved = e;

        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));   // 발동
        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));   // 정상 복귀
        await EvaluateFreshPollAsync(t.sut, 50m);

        resolved.Should().NotBeNull("정상 복귀 시 해제 이벤트가 발생한다");
        resolved!.EquipmentId.Should().Be("EQ-001");
        resolved.ParameterId.Should().Be("TEMP01");
    }

    [Fact]
    public async Task OnTagChange_does_not_resolve_when_never_triggered()
    {
        var t = BuildWithInterlock();
        SetupRule(t);
        await InitializeInterlockAsync(t.sut);
        var resolvedFired = false;
        t.sut.InterlockResolved += (_, _) => resolvedFired = true;

        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));   // 처음부터 정상

        resolvedFired.Should().BeFalse("발동한 적 없으면 해제 이벤트도 없다");
    }

    [Fact]
    public async Task OnTagChange_signals_once_and_retries_history_after_record_failure()
    {
        // 인터락 신호는 이력 DB보다 먼저 나가야 한다. 기록 실패는 같은 episode의 신호를 다시 내보내지 않고
        // 동일 EffectId로 다음 위반 샘플에서 이력만 재시도한다.
        var param = FdcParameter.Create("TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>())).ReturnsAsync(param);
        var dataRepo = new Mock<IFdcCollectDataRepository>();

        var ruleRepo = new Mock<IFdcInterlockRuleRepository>();
        var rule = FdcInterlockRule.Create("R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        ruleRepo.Setup(r => r.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { rule });

        // 첫 AddAsync는 DB 오류로 실패, 두 번째는 성공.
        var addCalls = 0;
        var attemptedHistoryIds = new List<string>();
        var historyRepo = new Mock<IFdcInterlockHistoryRepository>();
        historyRepo.Setup(r => r.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        historyRepo.Setup(r => r.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        historyRepo.Setup(r => r.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
                   .Callback<FdcInterlockHistory, CancellationToken>((history, _) => attemptedHistoryIds.Add(history.Id))
                   .Returns(() => ++addCalls == 1
                       ? Task.FromException(new InvalidOperationException("db down"))
                       : Task.CompletedTask);

        var interlock = new FdcInterlockService(ruleRepo.Object, historyRepo.Object);
        var sut = new FdcCollectorService(
            new FdcDataService(paramRepo.Object, dataRepo.Object),
            interlock,
            actionPort: new ConfirmedInterlockActionPort());
        await InitializeInterlockAsync(sut);

        var triggers = new List<FdcInterlockTriggeredEventArgs>();
        sut.InterlockTriggered += (_, args) => triggers.Add(args);

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));
        triggers.Should().ContainSingle("이력 DB 장애가 최초 인터락 신호를 억제하면 안 된다");

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 91.0, PlcQuality.Good));

        triggers.Should().ContainSingle("같은 위반 episode에서는 이력 재시도만 하고 신호는 중복하지 않는다");
        triggers[0].EffectId.Should().NotBeNullOrWhiteSpace();
        attemptedHistoryIds.Should().Equal(
            new[] { triggers[0].EffectId, triggers[0].EffectId },
            "이력 재시도는 최초 신호와 같은 stable EffectId를 사용해야 한다");
        historyRepo.Verify(r => r.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "다음 위반 샘플에서 이력 기록만 재시도한다");
    }

    [Fact]
    public async Task OnTagChange_keeps_monitoring_and_reasserts_an_effect_without_durable_evidence()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetAllUnresolvedAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        historyRepository.Setup(repository => repository.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FdcInterlockHistory>());
        historyRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: new ConfirmedInterlockActionPort());
        await InitializeInterlockAsync(collector);
        var triggered = new List<FdcInterlockTriggeredEventArgs>();
        var resolved = new List<FdcInterlockResolvedEventArgs>();
        collector.InterlockTriggered += (_, args) => triggered.Add(args);
        collector.InterlockResolved += (_, args) => resolved.Add(args);

        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        await EvaluateFreshPollAsync(collector, 50m);
        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 91m, PlcQuality.Good));

        triggered.Should().ContainSingle(
            "DB 장애 중에는 물리 action을 유지하고 새 episode를 만들지 않는다");
        resolved.Should().BeEmpty(
            "Prepared/Applied 증거 없이는 release 자체를 시도하지 않는다");
        collector.IsRunPermitted.Should().BeFalse(
            "DB 증거가 복구될 때까지 운전 허가는 닫되 PLC 감시는 계속해야 한다");
    }

    [Fact]
    public async Task OnTagChange_preserves_trigger_then_resolve_evidence_until_history_database_recovers()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        FdcInterlockHistory? durable = null;
        var addAttempts = 0;
        var persistenceOrder = new List<string>();
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetAllUnresolvedAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable is null
                ? Array.Empty<FdcInterlockHistory>()
                : new[] { durable });
        historyRepository.Setup(repository => repository.GetByIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable);
        historyRepository.Setup(repository => repository.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable is { IsResolved: false }
                ? new[] { durable }
                : Array.Empty<FdcInterlockHistory>());
        historyRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Returns<FdcInterlockHistory, CancellationToken>((history, _) =>
            {
                addAttempts++;
                persistenceOrder.Add($"add:{addAttempts}:{history.Id}");
                if (addAttempts <= 2)
                    return Task.FromException(new InvalidOperationException("db down"));
                durable = history;
                return Task.CompletedTask;
            });
        historyRepository.Setup(repository => repository.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, int, CancellationToken>((history, _, _) =>
                persistenceOrder.Add($"state:{history.EffectState}:{history.Id}"))
            .ReturnsAsync(true);

        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: new ConfirmedInterlockActionPort());
        await InitializeInterlockAsync(collector);
        var triggered = new List<FdcInterlockTriggeredEventArgs>();
        var resolved = new List<FdcInterlockResolvedEventArgs>();
        collector.InterlockTriggered += (_, args) => triggered.Add(args);
        collector.InterlockResolved += (_, args) => resolved.Add(args);

        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        await EvaluateFreshPollAsync(collector, 50m);

        triggered.Should().ContainSingle();
        resolved.Should().ContainSingle();
        durable.Should().NotBeNull("정상 전환은 pending Prepared 기록을 같은 EffectId로 즉시 재시도한다");

        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 49m, PlcQuality.Good));

        durable.Should().NotBeNull();
        durable!.Id.Should().Be(triggered[0].EffectId);
        durable.IsResolved.Should().BeTrue();
        persistenceOrder.Should().ContainInOrder(
            $"add:3:{triggered[0].EffectId}",
            $"state:Applied:{triggered[0].EffectId}",
            $"state:Resolved:{triggered[0].EffectId}");
        historyRepository.Verify(repository => repository.UpdateAsync(
            It.Is<FdcInterlockHistory>(history => history.Id == triggered[0].EffectId),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 48m, PlcQuality.Good));
        historyRepository.Verify(repository => repository.UpdateAsync(
            It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "완료된 lifecycle은 CAS 상태로 수렴한다");
    }

    [Fact]
    public async Task OnTagChange_reapplies_with_a_new_effect_when_violation_recurs_after_physical_release_but_before_db_resolution()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);

        var durable = new Dictionary<string, FdcInterlockHistory>(StringComparer.Ordinal);
        var terminalPersistenceAvailable = false;
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.Values.Where(effect => !effect.IsResolved).Select(CopyHistory).ToArray());
        historyRepository.Setup(repository => repository.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.Values.Where(effect => !effect.IsResolved).Select(CopyHistory).ToArray());
        historyRepository.Setup(repository => repository.GetByIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string effectId, CancellationToken _) =>
                durable.TryGetValue(effectId, out var effect) ? CopyHistory(effect) : null);
        historyRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, CancellationToken>((effect, _) =>
                durable.Add(effect.Id, CopyHistory(effect)))
            .Returns(Task.CompletedTask);
        historyRepository.Setup(repository => repository.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockHistory effect, int expectedVersion, CancellationToken _) =>
            {
                if (!durable.TryGetValue(effect.Id, out var current)
                    || current.Version != expectedVersion
                    || (effect.EffectState == FdcInterlockEffectState.Resolved
                        && !terminalPersistenceAvailable))
                    return false;
                durable[effect.Id] = CopyHistory(effect);
                return true;
            });

        var applied = new List<FdcInterlockActionRequest>();
        var released = new List<FdcInterlockReleaseRequest>();
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockActionRequest, CancellationToken>((request, _) => applied.Add(request))
            .ReturnsAsync((FdcInterlockActionRequest request, CancellationToken _) =>
                FdcInterlockActionResult.Confirmed($"apply:{request.EffectId}"));
        action.Setup(port => port.ReconcileAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockActionRequest request, CancellationToken _) =>
                FdcInterlockActionResult.Confirmed($"reconcile:{request.EffectId}"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockReleaseRequest, CancellationToken>((request, _) => released.Add(request))
            .ReturnsAsync((FdcInterlockReleaseRequest request, CancellationToken _) =>
                FdcInterlockReleaseResult.Confirmed($"release:{request.EffectId}"));

        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: action.Object);
        var resolved = new List<FdcInterlockResolvedEventArgs>();
        collector.InterlockResolved += (_, args) => resolved.Add(args);
        await InitializeInterlockAsync(collector);

        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));
        var firstEffectId = applied.Single().EffectId;
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        await EvaluateFreshPollAsync(collector, 50m);
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 91m, PlcQuality.Good));

        applied.Should().HaveCount(2);
        applied.Select(request => request.EffectId).Should().OnlyHaveUniqueItems();
        released.Should().ContainSingle(request => request.EffectId == firstEffectId);
        resolved.Should().BeEmpty("terminal DB evidence is still unavailable");
        durable[firstEffectId].IsResolved.Should().BeFalse();

        terminalPersistenceAvailable = true;
        await collector.RetryPendingEffectPersistenceAsync();

        durable[firstEffectId].IsResolved.Should().BeTrue();
        durable[applied[1].EffectId].IsResolved.Should().BeFalse();
        resolved.Should().ContainSingle(args => args.EffectId == firstEffectId);
    }

    [Fact]
    public async Task OnTagChange_after_restart_resolves_durable_interlock_on_first_normal_sample()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(r => r.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(r => r.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        var durable = FdcInterlockHistory.Create(
            "H-OPEN", "R1", "EQ-001", "TEMP01", 90m, "STOP", "open", DateTime.UtcNow).Value;
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(r => r.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.IsResolved
                ? Array.Empty<FdcInterlockHistory>()
                : new[] { durable });
        historyRepository.Setup(r => r.GetByIdAsync(
                "H-OPEN", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable);
        historyRepository.Setup(r => r.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.IsResolved
                ? Array.Empty<FdcInterlockHistory>()
                : new[] { durable });
        historyRepository.Setup(r => r.UpdateAsync(
                durable, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: new ConfirmedInterlockActionPort());
        var resolved = 0;
        collector.InterlockResolved += (_, _) => resolved++;
        await InitializeInterlockAsync(collector);

        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        await EvaluateFreshPollAsync(collector, 50m);

        durable.IsResolved.Should().BeTrue();
        resolved.Should().Be(1, "the first normal sample after restart must clear durable open state");
        historyRepository.Verify(r => r.UpdateAsync(
            durable, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task OnTagChange_after_restart_does_not_duplicate_a_durable_interlock()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(r => r.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(r => r.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        var durable = FdcInterlockHistory.Create(
            "H-OPEN", "R1", "EQ-001", "TEMP01", 90m, "STOP", "open", DateTime.UtcNow).Value;
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(r => r.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { durable });
        historyRepository.Setup(r => r.GetUnresolvedAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { durable });
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: new ConfirmedInterlockActionPort());
        await InitializeInterlockAsync(collector, initialValue: 95m);

        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 95m, PlcQuality.Good));

        historyRepository.Verify(r => r.AddAsync(
            It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()), Times.Never);
        durable.IsResolved.Should().BeFalse();
    }

    // ── 구독 연결 end-to-end (PlcDeviceInterface + FdcCollectorService) ──────────

    [Fact]
    public async Task Device_subscription_normalizes_and_records_with_domain_equipment_id()
    {
        // NexaLogic 연결 모킹: SubscriptionProvider.StartAsync가 onEvent 콜백을 포착하도록 설정
        var endpoint = new PlcEndpoint("EP1", PlcDriverKind.OpcUa, "opc.tcp://host:4840");
        Func<PlcTagChangeEvent, Task>? onEvent = null;
        var subProvider = new Mock<IPlcSubscriptionProvider>();
        subProvider.Setup(s => s.StartAsync(It.IsAny<PlcEndpoint>(), It.IsAny<IEnumerable<PlcSubscription>>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(), It.IsAny<CancellationToken>()))
            .Callback<PlcEndpoint, IEnumerable<PlcSubscription>, Func<PlcTagChangeEvent, Task>, CancellationToken>(
                (_, _, cb, _) => onEvent = cb)
            .Returns(Task.CompletedTask);

        var conn = new Mock<IPlcConnection>();
        conn.SetupGet(c => c.Endpoint).Returns(endpoint);
        conn.SetupGet(c => c.SubscriptionProvider).Returns(subProvider.Object);
        conn.Setup(c => c.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var driver = new Mock<IPlcDriver>();
        driver.SetupGet(d => d.Kind).Returns(PlcDriverKind.OpcUa);
        driver.SetupGet(d => d.Name).Returns("fake-opcua");
        driver.Setup(d => d.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(conn.Object);

        var device = new PlcDeviceInterface("EP1", endpoint, driver.Object);
        await device.InitializeAsync();

        var param = FdcParameter.Create("TEMP01", "Temp", "EQ-001", "C", 0m, 100m).Value;
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>())).ReturnsAsync(param);
        FdcCollectData? saved = null;
        var dataRepo = new Mock<IFdcCollectDataRepository>();
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
                .Callback<FdcCollectData, CancellationToken>((d, _) => saved = d)
                .Returns(Task.CompletedTask);
        var collector = new FdcCollectorService(new FdcDataService(paramRepo.Object, dataRepo.Object));

        var sub = new PlcSubscription("EP1::sub", "EP1", new[] { "TEMP01" }, TimeSpan.FromSeconds(1));
        await device.SubscribeAsync(
            new[] { sub },
            sample => collector.OnTagChangeAsync("EQ-001", sample));

        onEvent.Should().NotBeNull("구독이 SubscriptionProvider에 연결된다");
        await onEvent!(new PlcTagChangeEvent("e", "EP1", "TEMP01", "ns", null, 42.0,
            PlcQuality.Good, DateTimeOffset.UnixEpoch, "polling", true));

        saved.Should().NotBeNull("구독 콜백이 수집 데이터로 적재된다");
        saved!.EquipmentId.Should().Be("EQ-001", "endpoint id와 domain equipment id를 혼동하지 않는다");
        saved.ParameterId.Should().Be("TEMP01");
        saved.Value.Should().Be(42m);
        saved.Quality.Should().Be("Good");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public async Task Device_subscription_marks_invalid_good_payload_bad_and_revokes_interlock_permit(object? payload)
    {
        var endpoint = new PlcEndpoint("EP1", PlcDriverKind.OpcUa, "opc.tcp://host:4840");
        Func<PlcTagChangeEvent, Task>? onEvent = null;
        var subProvider = new Mock<IPlcSubscriptionProvider>();
        subProvider.Setup(provider => provider.StartAsync(
                It.IsAny<PlcEndpoint>(),
                It.IsAny<IEnumerable<PlcSubscription>>(),
                It.IsAny<Func<PlcTagChangeEvent, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<PlcEndpoint, IEnumerable<PlcSubscription>, Func<PlcTagChangeEvent, Task>, CancellationToken>(
                (_, _, callback, _) => onEvent = callback)
            .Returns(Task.CompletedTask);

        var connection = new Mock<IPlcConnection>();
        connection.SetupGet(candidate => candidate.Endpoint).Returns(endpoint);
        connection.SetupGet(candidate => candidate.SubscriptionProvider).Returns(subProvider.Object);
        connection.Setup(candidate => candidate.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var driver = new Mock<IPlcDriver>();
        driver.SetupGet(candidate => candidate.Kind).Returns(PlcDriverKind.OpcUa);
        driver.SetupGet(candidate => candidate.Name).Returns("fake-opcua");
        driver.Setup(candidate => candidate.ConnectAsync(
                It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection.Object);

        var device = new PlcDeviceInterface("EP1", endpoint, driver.Object);
        await device.InitializeAsync();

        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);

        FdcCollectData? saved = null;
        var dataRepository = new Mock<IFdcCollectDataRepository>();
        dataRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
            .Callback<FdcCollectData, CancellationToken>((data, _) => saved = data)
            .Returns(Task.CompletedTask);

        var rule = FdcInterlockRule.Create(
            "RULE-LOW", "Underflow", "EQ-001", "TEMP01", "LT", 10m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        var historyRepository = EmptyInterlockHistory();
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, dataRepository.Object),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: new ConfirmedInterlockActionPort());
        var interlockTriggered = false;
        collector.InterlockTriggered += (_, _) => interlockTriggered = true;
        await InitializeInterlockAsync(collector);

        await device.SubscribeAsync(
            new[] { new PlcSubscription("EP1::sub", "EP1", new[] { "TEMP01" }, TimeSpan.FromSeconds(1)) },
            sample => collector.OnTagChangeAsync("EQ-001", sample));
        var act = () => onEvent!(new PlcTagChangeEvent(
            "event-1", "EP1", "TEMP01", "ns", null, payload,
            PlcQuality.Good, DateTimeOffset.UnixEpoch, "polling", true));

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*quality is 'Bad'*");

        saved.Should().NotBeNull("invalid transport payloads remain observable as bad samples");
        saved!.Value.Should().Be(0m);
        saved.Quality.Should().Be("Bad", "an invalid payload cannot retain transport-reported Good quality");
        interlockTriggered.Should().BeFalse("the fallback zero is not a valid process value");
        collector.IsRunPermitted.Should().BeFalse();
        ruleRepository.Verify(repository => repository.GetActiveRulesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Device_subscription_throws_when_device_not_initialized()
    {
        var device = new PlcDeviceInterface("EP1",
            new PlcEndpoint("EP1", PlcDriverKind.OpcUa, "opc.tcp://h:1"),
            Mock.Of<IPlcDriver>(driver => driver.Kind == PlcDriverKind.OpcUa));

        var act = () => device.SubscribeAsync(Array.Empty<PlcSubscription>(), _ => Task.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>("초기화 전에는 구독을 걸 수 없다");
    }

    // ── 알람 평가 연결 (§10.4.1) ────────────────────────────────────────────────

    private static FdcCollectorService BuildWithAlarm(Mock<IFdcAlarmConfigRepository> cfgRepo)
    {
        var param = FdcParameter.Create("TEMP01", "Temp", "EQ-001", "C", 0m, 100m).Value;
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>())).ReturnsAsync(param);
        var dataRepo = new Mock<IFdcCollectDataRepository>();
        var alarmSvc = new FdcAlarmService(cfgRepo.Object);   // history repo 없음
        return new FdcCollectorService(
            new FdcDataService(paramRepo.Object, dataRepo.Object), interlockService: null, alarmService: alarmSvc);
    }

    private static void SetupAlarm(Mock<IFdcAlarmConfigRepository> cfgRepo, string level, string op, decimal threshold)
    {
        var cfg = FdcAlarmConfig.Create("A1", "EQ-001", "TEMP01", level, op, threshold).Value;
        cfgRepo.Setup(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { cfg });
    }

    [Fact]
    public async Task OnTagChange_raises_alarm_once_then_clears_on_normal()
    {
        var cfgRepo = new Mock<IFdcAlarmConfigRepository>();
        var sut = BuildWithAlarm(cfgRepo);
        SetupAlarm(cfgRepo, "Warning", "GT", 80m);

        var raised = 0;
        FdcAlarmClearedEventArgs? cleared = null;
        sut.AlarmRaised += (_, _) => raised++;
        sut.AlarmCleared += (_, e) => cleared = e;

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));  // 발생
        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 95.0, PlcQuality.Good));  // 발생 중복(억제)
        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));  // 정상 복귀

        raised.Should().Be(1, "발생 중에는 중복 통지하지 않는다");
        cleared.Should().NotBeNull("정상 복귀 시 해제 이벤트가 발생한다");
        cleared!.ParameterId.Should().Be("TEMP01");
    }

    [Fact]
    public async Task OnTagChange_reports_critical_when_multiple_levels_match()
    {
        var warn = FdcAlarmConfig.Create("AW", "EQ-001", "TEMP01", "Warning", "GT", 70m).Value;
        var crit = FdcAlarmConfig.Create("AC", "EQ-001", "TEMP01", "Critical", "GT", 90m).Value;
        var cfgRepo = new Mock<IFdcAlarmConfigRepository>();
        cfgRepo.Setup(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { warn, crit });
        var sut = BuildWithAlarm(cfgRepo);

        FdcAlarmRaisedEventArgs? raised = null;
        sut.AlarmRaised += (_, e) => raised = e;

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 95.0, PlcQuality.Good));

        raised.Should().NotBeNull();
        raised!.Alarm.AlarmLevel.Should().Be("Critical", "여러 레벨이 잡히면 가장 심각한 것을 통지한다");
    }

    [Fact]
    public async Task OnTagChange_escalates_alarm_from_warning_to_critical()
    {
        var warn = FdcAlarmConfig.Create("AW", "EQ-001", "TEMP01", "Warning", "GT", 70m).Value;
        var crit = FdcAlarmConfig.Create("AC", "EQ-001", "TEMP01", "Critical", "GT", 90m).Value;
        var cfgRepo = new Mock<IFdcAlarmConfigRepository>();
        cfgRepo.Setup(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { warn, crit });
        var sut = BuildWithAlarm(cfgRepo);

        var raised = new List<string>();
        sut.AlarmRaised += (_, e) => raised.Add(e.Alarm.AlarmLevel);

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 75.0, PlcQuality.Good));  // Warning만 (70<75<90)
        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 95.0, PlcQuality.Good));  // Critical로 악화

        raised.Should().Equal(new[] { "Warning", "Critical" },
            "Warning 발생 후 Critical로 악화되면 심각도 상승을 다시 통지한다");
    }

    [Fact]
    public async Task OnTagChange_clears_only_normalized_config_while_lower_alarm_remains_active()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(r => r.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var warning = FdcAlarmConfig.Create(
            "AW", "EQ-001", "TEMP01", "Warning", "GT", 70m).Value;
        var critical = FdcAlarmConfig.Create(
            "AC", "EQ-001", "TEMP01", "Critical", "GT", 90m).Value;
        var configRepository = new Mock<IFdcAlarmConfigRepository>();
        configRepository.Setup(r => r.GetActiveConfigsAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { warning, critical });
        var histories = new List<FdcAlarmHistory>();
        var historyRepository = new Mock<IFdcAlarmHistoryRepository>();
        historyRepository.Setup(r => r.GetOpenAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => histories.Where(static history => !history.IsCleared).ToArray());
        historyRepository.Setup(r => r.AddAsync(
                It.IsAny<FdcAlarmHistory>(), It.IsAny<CancellationToken>()))
            .Callback<FdcAlarmHistory, CancellationToken>((history, _) => histories.Add(history))
            .Returns(Task.CompletedTask);
        historyRepository.Setup(r => r.UpdateAsync(
                It.IsAny<FdcAlarmHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            alarmService: new FdcAlarmService(configRepository.Object, historyRepository.Object));
        var cleared = new List<string>();
        collector.AlarmCleared += (_, args) => cleared.Add(args.AlarmConfigId);

        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 95m, PlcQuality.Good));
        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 80m, PlcQuality.Good));

        histories.Should().HaveCount(2);
        histories.Single(history => history.AlarmConfigId == "AC").IsCleared.Should().BeTrue();
        histories.Single(history => history.AlarmConfigId == "AW").IsCleared.Should().BeFalse();
        cleared.Should().Equal("AC");
    }

    [Fact]
    public async Task OnTagChange_after_restart_clears_durable_alarm_without_duplicate_history()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(r => r.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var config = FdcAlarmConfig.Create(
            "A1", "EQ-001", "TEMP01", "Critical", "GT", 80m).Value;
        var configRepository = new Mock<IFdcAlarmConfigRepository>();
        configRepository.Setup(r => r.GetActiveConfigsAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { config });
        var durable = FdcAlarmHistory.Create(
            "A-OPEN", "A1", "EQ-001", "TEMP01", "Critical", 90m, "open", DateTime.UtcNow).Value;
        var historyRepository = new Mock<IFdcAlarmHistoryRepository>();
        historyRepository.Setup(r => r.GetOpenAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.IsCleared
                ? Array.Empty<FdcAlarmHistory>()
                : new[] { durable });
        historyRepository.Setup(r => r.UpdateAsync(
                durable, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            alarmService: new FdcAlarmService(configRepository.Object, historyRepository.Object));
        var cleared = 0;
        collector.AlarmCleared += (_, _) => cleared++;

        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 50m, PlcQuality.Good));

        durable.IsCleared.Should().BeTrue();
        cleared.Should().Be(1);
        historyRepository.Verify(r => r.AddAsync(
            It.IsAny<FdcAlarmHistory>(), It.IsAny<CancellationToken>()), Times.Never);
        historyRepository.Verify(r => r.UpdateAsync(
            durable, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 품질 게이팅: Bad/Disconnected 읽기(value=0)가 거짓 해제/거짓 발동하지 않음 ──────────

    [Fact]
    public async Task OnTagChange_does_not_resolve_interlock_on_bad_quality()
    {
        var t = BuildWithInterlock();
        SetupRule(t);   // GT 80 STOP
        await InitializeInterlockAsync(t.sut);
        FdcInterlockResolvedEventArgs? resolved = null;
        t.sut.InterlockResolved += (_, e) => resolved = e;

        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));  // 발동
        var act = () => t.sut.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", null, PlcQuality.Disconnected));  // 끊김→0

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*quality is 'Bad'*");

        resolved.Should().BeNull("Bad/Disconnected 품질 읽기는 활성 인터락을 거짓 해제하지 않는다");
        t.sut.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task OnTagChange_does_not_trigger_low_interlock_on_bad_quality()
    {
        var t = BuildWithInterlock();
        var rule = FdcInterlockRule.Create("R1", "Underflow", "EQ-001", "TEMP01", "LT", 10m, "STOP", 1).Value;
        t.ruleRepo.Setup(r => r.GetByEquipmentAsync("EQ-001", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { rule });
        await InitializeInterlockAsync(t.sut);
        var fired = false;
        t.sut.InterlockTriggered += (_, _) => fired = true;

        var act = () => t.sut.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", null, PlcQuality.Disconnected));  // 0, Bad

        await act.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*quality is 'Bad'*");

        fired.Should().BeFalse("Bad 품질로 0이 된 값은 저값 인터락(LT 10)을 거짓 발동시키지 않는다");
        t.sut.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Pending_manual_release_retries_only_from_a_fresh_completed_poll()
    {
        var parameter = FdcParameter.Create(
            "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value;
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameter);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var historyRepository = EmptyInterlockHistory();
        var releaseAttempts = 0;
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockActionRequest request, CancellationToken _) =>
                FdcInterlockActionResult.Confirmed($"apply:{request.EffectId}"));
        action.Setup(port => port.ReconcileAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockActionRequest request, CancellationToken _) =>
                FdcInterlockActionResult.Confirmed($"reconcile:{request.EffectId}"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockReleaseRequest request, CancellationToken _) =>
                ++releaseAttempts == 1
                    ? new FdcInterlockReleaseResult(
                        Acknowledged: false,
                        ReadbackConfirmed: false,
                        ManualResetRequired: true,
                        AcknowledgementId: null,
                        Detail: "operator reset is still required")
                    : FdcInterlockReleaseResult.Confirmed($"release:{request.EffectId}"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: action.Object);
        var resolved = new List<FdcInterlockResolvedEventArgs>();
        collector.InterlockResolved += (_, args) => resolved.Add(args);
        await InitializeInterlockAsync(collector);

        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 50m, PlcQuality.Good));

        collector.IsRunPermitted.Should().BeFalse();
        releaseAttempts.Should().Be(0,
            "live tag changes may record normalization but cannot physically release");
        resolved.Should().BeEmpty();

        await EvaluateFreshPollAsync(collector, 50m);
        releaseAttempts.Should().Be(1);

        await collector.RetryPendingEffectPersistenceAsync();

        releaseAttempts.Should().Be(1,
            "the DB retry supervisor must never release from a cached condition value");
        resolved.Should().BeEmpty();
        collector.IsRunPermitted.Should().BeFalse();

        var accepted = await collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 50m, PlcQuality.Good)],
            isSnapshotCurrent: () => true);

        accepted.Should().BeTrue();
        releaseAttempts.Should().Be(2,
            "a fully delivered and still-current PLC poll may re-check a manual reset without a tag change");
        resolved.Should().ContainSingle();
        collector.IsRunPermitted.Should().BeTrue(
            "admission can reopen only after physical release and terminal evidence both converge");
    }

    [Fact]
    public async Task Stale_completed_poll_does_not_retry_pending_manual_release()
    {
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var releaseAttempts = 0;
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-confirmed"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                releaseAttempts++;
                return new FdcInterlockReleaseResult(false, false, true, null, "manual reset pending");
            });
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, EmptyInterlockHistory().Object),
            actionPort: action.Object);
        await InitializeInterlockAsync(collector);
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        releaseAttempts.Should().Be(0);

        await EvaluateFreshPollAsync(collector, 50m);
        releaseAttempts.Should().Be(1);

        var accepted = await collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 50m, PlcQuality.Good)],
            isSnapshotCurrent: () => false);

        accepted.Should().BeFalse();
        releaseAttempts.Should().Be(1);
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Completed_poll_preflights_every_interlock_quality_before_releasing_an_earlier_normal_input()
    {
        var topology = new[] { new FdcInterlockTopology("EQ-001", ["TEMP01", "PRESS01"]) };
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string parameterId, CancellationToken _) => FdcParameter.Create(
                parameterId, parameterId, "EQ-001", "unit", 0m, 100m).Value);
        var rules = new[]
        {
            FdcInterlockRule.Create(
                "R-TEMP", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP.TEMP", 1).Value,
            FdcInterlockRule.Create(
                "R-PRESS", "OverPressure", "EQ-001", "PRESS01", "GT", 80m, "STOP.PRESS", 1).Value,
        };
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);
        var historyRepository = EmptyInterlockHistory();
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-confirmed"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockReleaseResult.Confirmed("release-confirmed"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: action.Object);
        await collector.InitializeInterlockRuntimeAsync(topology);
        await collector.EvaluateInitialSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 20m, PlcQuality.Good), Event("PRESS01", 20m, PlcQuality.Good)]);
        collector.CompleteInterlockRuntimeInitialization();
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));

        var evaluate = () => collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 50m, PlcQuality.Good), Event("PRESS01", 0m, PlcQuality.Bad)],
            isSnapshotCurrent: static () => true);

        await evaluate.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*PRESS01*quality*completed PLC poll*");
        action.Verify(port => port.ReleaseAsync(
            It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "the entire completed poll must be safe before any physical effect is released");
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Completed_poll_final_freshness_failure_revokes_an_open_permit_even_without_an_active_effect()
    {
        var fixture = BuildWithInterlock();
        SetupRule(fixture);
        await InitializeInterlockAsync(fixture.sut);
        fixture.sut.IsRunPermitted.Should().BeTrue();
        var predicateCalls = 0;
        var runtimeFaults = 0;
        fixture.sut.RuntimeFaulted += _ => runtimeFaults++;

        var evaluate = () => fixture.sut.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 20m, PlcQuality.Good)],
            isSnapshotCurrent: () => Interlocked.Increment(ref predicateCalls) < 6
                ? true
                : throw new FdcInterlockRuntimeUnavailableException(
                    "endpoint freshness expired at the final completed-poll fence"));

        await evaluate.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*freshness expired*final completed-poll fence*");
        fixture.sut.IsRunPermitted.Should().BeFalse();
        runtimeFaults.Should().Be(1);
    }

    [Fact]
    public async Task Completed_poll_that_becomes_stale_during_condition_persistence_never_releases_or_reopens_admission()
    {
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);

        var persistenceEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPersistence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var historyRepository = EmptyInterlockHistory();
        historyRepository.Setup(repository => repository.UpdateAsync(
                It.Is<FdcInterlockHistory>(history =>
                    history.EffectState == FdcInterlockEffectState.ConditionNormalized),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (FdcInterlockHistory _, int _, CancellationToken ct) =>
            {
                persistenceEntered.TrySetResult(true);
                await allowPersistence.Task.WaitAsync(ct);
                return true;
            });

        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-confirmed"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockReleaseResult.Confirmed("release-confirmed"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: action.Object);
        await InitializeInterlockAsync(collector);
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));

        var snapshotCurrent = 1;
        var evaluation = collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 50m, PlcQuality.Good)],
            isSnapshotCurrent: () => Volatile.Read(ref snapshotCurrent) == 1);
        await persistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref snapshotCurrent, 0);
        allowPersistence.TrySetResult(true);

        var accepted = await evaluation;

        accepted.Should().BeFalse();
        action.Verify(port => port.ReleaseAsync(
            It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_poll_never_releases_when_target_or_peer_endpoint_becomes_stale_during_db_persistence(
        bool peerEndpointBecomesStale)
    {
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                FdcInterlockRule.Create(
                    "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value
            ]);

        var persistenceEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPersistence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var historyRepository = EmptyInterlockHistory();
        historyRepository.Setup(repository => repository.UpdateAsync(
                It.Is<FdcInterlockHistory>(history =>
                    history.EffectState == FdcInterlockEffectState.ConditionNormalized),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (FdcInterlockHistory _, int _, CancellationToken ct) =>
            {
                persistenceEntered.TrySetResult(true);
                await allowPersistence.Task.WaitAsync(ct);
                return true;
            });

        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-confirmed"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockReleaseResult.Confirmed("release-confirmed"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, historyRepository.Object),
            actionPort: action.Object);
        await InitializeInterlockAsync(collector);
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));

        var snapshot = new PlcCompletedPollSnapshot(
            subscriptionGeneration: 1,
            startedPollCount: 1,
            completedPollCount: 1,
            completedAt: DateTimeOffset.UtcNow,
            values:
            [
                new PlcTagValue(
                    "TEMP01", 50m, PlcQuality.Good, DateTimeOffset.UtcNow, "test")
            ]);
        var freshnessExpired = 0;
        var targetHealth = new Mock<IPlcCompletedPollSnapshotRuntimeHealth>();
        targetHealth.SetupGet(health => health.SubscriptionGeneration).Returns(1);
        targetHealth.SetupGet(health => health.IsRunning).Returns(true);
        targetHealth.SetupGet(health => health.StartedPollCount).Returns(1);
        targetHealth.SetupGet(health => health.CompletedPollCount).Returns(1);
        targetHealth.SetupGet(health => health.LatestCompletedPollSnapshot).Returns(snapshot);
        targetHealth.SetupGet(health => health.TimeSinceLastCompletedPoll)
            .Returns(() => Volatile.Read(ref freshnessExpired) == 1 && !peerEndpointBecomesStale
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.Zero);
        var peerHealth = new Mock<IPlcCompletedPollSnapshotRuntimeHealth>();
        peerHealth.SetupGet(health => health.SubscriptionGeneration).Returns(7);
        peerHealth.SetupGet(health => health.IsRunning).Returns(true);
        peerHealth.SetupGet(health => health.StartedPollCount).Returns(1);
        peerHealth.SetupGet(health => health.CompletedPollCount).Returns(1);
        peerHealth.SetupGet(health => health.TimeSinceLastCompletedPoll)
            .Returns(() => Volatile.Read(ref freshnessExpired) == 1 && peerEndpointBecomesStale
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.Zero);
        var targetRegistration = new FdcCollectionWorker.RuntimeHealthRegistration(
            "EP-TARGET", "EQ-001", new HashSet<string>(["TEMP01"]), targetHealth.Object,
            1, TimeSpan.FromSeconds(1), Task.CompletedTask, 0);
        var peerRegistration = new FdcCollectionWorker.RuntimeHealthRegistration(
            "EP-PEER", "EQ-002", new HashSet<string>(["OTHER01"]), peerHealth.Object,
            7, TimeSpan.FromSeconds(1), Task.CompletedTask, 0);
        FdcCollectionWorker.RuntimeHealthRegistration[] registrations =
            [targetRegistration, peerRegistration];

        var evaluation = collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", 50m, PlcQuality.Good)],
            isSnapshotCurrent: () => FdcCollectionWorker.IsReleaseSnapshotCurrentAndFresh(
                targetRegistration, snapshot, registrations));
        await persistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref freshnessExpired, 1);
        allowPersistence.TrySetResult(true);

        var awaitEvaluation = async () => await evaluation;
        await awaitEvaluation.Should().ThrowAsync<FdcInterlockRuntimeUnavailableException>()
            .WithMessage(peerEndpointBecomesStale ? "*EP-PEER*stale*" : "*EP-TARGET*stale*");
        action.Verify(port => port.ReleaseAsync(
            It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        collector.IsRunPermitted.Should().BeFalse();
    }

    [Fact]
    public async Task Release_cancellation_faults_runtime_and_revokes_admission_immediately()
    {
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-confirmed"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("release cancelled"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, EmptyInterlockHistory().Object),
            actionPort: action.Object);
        var runtimeFaults = 0;
        collector.RuntimeFaulted += _ => runtimeFaults++;
        await InitializeInterlockAsync(collector);
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));

        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        var release = () => EvaluateFreshPollAsync(collector, 50m);

        await release.Should().ThrowAsync<OperationCanceledException>();
        collector.IsRunPermitted.Should().BeFalse();
        runtimeFaults.Should().Be(1,
            "an unknown late release outcome requires a full runtime reconciliation before restart");
    }

    [Fact]
    public async Task Release_timeout_is_a_terminal_fault_instead_of_a_retryable_manual_wait()
    {
        var parameterRepository = new Mock<IFdcParameterRepository>();
        parameterRepository.Setup(repository => repository.GetByIdAsync(
                "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcParameter.Create(
                "TEMP01", "Temperature", "EQ-001", "C", 0m, 100m).Value);
        var rule = FdcInterlockRule.Create(
            "R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        var ruleRepository = new Mock<IFdcInterlockRuleRepository>();
        ruleRepository.Setup(repository => repository.GetByEquipmentAsync(
                "EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);
        var action = new Mock<IFdcInterlockActionPort>();
        action.Setup(port => port.CheckReadyAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));
        action.Setup(port => port.ApplyAsync(
                It.IsAny<FdcInterlockActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdcInterlockActionResult.Confirmed("apply-confirmed"));
        action.Setup(port => port.ReleaseAsync(
                It.IsAny<FdcInterlockReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("controller deadline elapsed"));
        var collector = new FdcCollectorService(
            new FdcDataService(parameterRepository.Object, Mock.Of<IFdcCollectDataRepository>()),
            new FdcInterlockService(ruleRepository.Object, EmptyInterlockHistory().Object),
            actionPort: action.Object);
        Exception? runtimeFault = null;
        collector.RuntimeFaulted += failure => runtimeFault = failure;
        await InitializeInterlockAsync(collector);
        await collector.OnTagChangeAsync("EQ-001", Event("TEMP01", 90m, PlcQuality.Good));

        await collector.OnTagChangeAsync(
            "EQ-001", Event("TEMP01", 50m, PlcQuality.Good));
        var release = () => EvaluateFreshPollAsync(collector, 50m);

        await release.Should().ThrowAsync<FdcInterlockActionFailedException>()
            .WithMessage("*unknown physical outcome*");
        runtimeFault.Should().BeOfType<FdcInterlockActionFailedException>();
        collector.IsRunPermitted.Should().BeFalse();
    }

    private static async Task InitializeInterlockAsync(
        FdcCollectorService collector,
        decimal initialValue = 20m)
    {
        await collector.InitializeInterlockRuntimeAsync(InterlockTopology);
        await collector.EvaluateInitialSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", initialValue, PlcQuality.Good)]);
        collector.CompleteInterlockRuntimeInitialization();
    }

    private static Task<bool> EvaluateFreshPollAsync(
        FdcCollectorService collector,
        decimal value) =>
        collector.EvaluateCompletedPollSnapshotAsync(
            "EQ-001",
            [Event("TEMP01", value, PlcQuality.Good)],
            isSnapshotCurrent: static () => true);

    private static Mock<IFdcInterlockHistoryRepository> EmptyInterlockHistory(
        IReadOnlyList<FdcInterlockHistory>? open = null)
    {
        var repository = new Mock<IFdcInterlockHistoryRepository>();
        var durable = (open ?? Array.Empty<FdcInterlockHistory>())
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        repository.Setup(x => x.GetAllUnresolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.Values.Where(item => !item.IsResolved).ToArray());
        repository.Setup(x => x.GetUnresolvedAsync("EQ-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable.Values.Where(item => !item.IsResolved).ToArray());
        repository.Setup(x => x.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => durable.GetValueOrDefault(id));
        repository.Setup(x => x.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, CancellationToken>((item, _) => durable[item.Id] = item)
            .Returns(Task.CompletedTask);
        repository.Setup(x => x.UpdateAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return repository;
    }

    private static FdcInterlockHistory CopyHistory(FdcInterlockHistory history) =>
        FdcInterlockHistory.Restore(
            history.Id,
            history.RuleId,
            history.EquipmentId,
            history.ParameterId,
            history.TriggerValue,
            history.Action,
            history.Message,
            history.TriggeredAt,
            history.ResolvedAt,
            history.IsResolved,
            history.CreatedBy,
            history.CreatedAt,
            history.UpdatedBy,
            history.UpdatedAt,
            history.EffectState,
            history.ApplyAcknowledgementId,
            history.ApplyConfirmedAt,
            history.ConditionNormalizedAt,
            history.ConditionNormalizedValue,
            history.ReleaseAcknowledgementId,
            history.ReleaseConfirmedAt,
            history.LastError,
            history.Version);

    private sealed class ConfirmedInterlockActionPort : IFdcInterlockActionPort
    {
        public Task<FdcInterlockActionReadiness> CheckReadyAsync(
            IReadOnlyCollection<string> requiredActions,
            CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockActionReadiness.ReadyWithEvidence(
                aggregateEffectOwnershipConfirmed: true,
                runtimeFencePersistenceConfirmed: true));

        public Task<FdcInterlockActionResult> ApplyAsync(
            FdcInterlockActionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockActionResult.Confirmed($"test:{request.EffectId}"));

        public Task<FdcInterlockActionResult> ReconcileAsync(FdcInterlockActionRequest request, CancellationToken ct = default) =>
            ApplyAsync(request, ct);

        public Task<FdcInterlockReleaseResult> ReleaseAsync(FdcInterlockReleaseRequest request, CancellationToken ct = default) =>
            Task.FromResult(FdcInterlockReleaseResult.Confirmed($"release:{request.EffectId}"));
    }
}
