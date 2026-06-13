using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexusLogic.Plc.Abstractions.Interfaces;
using NexusLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Fdc;

/// <summary>§10.4 — OPC-UA 태그 변경 이벤트(NexusLogic PlcTagChangeEvent)를 FDC 수집 데이터로
/// 적재하는 오케스트레이터의 품질/값 변환과 저장 연결을 검증한다.</summary>
public sealed class FdcCollectorServiceTests
{
    private static PlcTagChangeEvent Event(string tag, object? after, PlcQuality quality) =>
        new("ev-1", "EQ-001", tag, $"ns=2;s={tag}", Before: null, After: after,
            quality, OccurredAt: DateTimeOffset.UnixEpoch, Source: "polling", IsChanged: true);

    [Theory]
    [InlineData(PlcQuality.Good, "Good")]
    [InlineData(PlcQuality.Uncertain, "Uncertain")]
    [InlineData(PlcQuality.Bad, "Bad")]
    [InlineData(PlcQuality.Timeout, "Bad")]
    [InlineData(PlcQuality.Disconnected, "Bad")]
    [InlineData(PlcQuality.NotSupported, "Bad")]
    public void MapQuality_maps_plc_quality_to_fdc_quality(PlcQuality input, string expected)
        => FdcCollectorService.MapQuality(input).Should().Be(expected);

    [Fact]
    public void ToDecimal_handles_null_numeric_and_unparsable()
    {
        FdcCollectorService.ToDecimal(null).Should().Be(0m, "null 값은 0으로 처리한다");
        FdcCollectorService.ToDecimal(42).Should().Be(42m);
        FdcCollectorService.ToDecimal(55.5).Should().Be(55.5m);
        FdcCollectorService.ToDecimal("12.25").Should().Be(12.25m);
        FdcCollectorService.ToDecimal("not-a-number").Should().Be(0m, "변환 불가 값은 0으로 처리한다");
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
        var interlock = new FdcInterlockService(ruleRepo.Object);
        return (new FdcCollectorService(dataService, interlock), ruleRepo);
    }

    [Fact]
    public async Task OnTagChange_raises_event_when_interlock_rule_triggers()
    {
        var (sut, ruleRepo) = BuildWithInterlock();
        var rule = FdcInterlockRule.Create("R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        ruleRepo.Setup(r => r.GetActiveRulesAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { rule });

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
        ruleRepo.Setup(r => r.GetActiveRulesAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { rule });

        var fired = false;
        sut.InterlockTriggered += (_, _) => fired = true;

        await sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));

        fired.Should().BeFalse("임계치 이내면 인터락 이벤트가 발생하지 않는다");
    }

    private static void SetupRule((FdcCollectorService sut, Mock<IFdcInterlockRuleRepository> ruleRepo) t)
    {
        var rule = FdcInterlockRule.Create("R1", "OverTemp", "EQ-001", "TEMP01", "GT", 80m, "STOP", 1).Value;
        t.ruleRepo.Setup(r => r.GetActiveRulesAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { rule });
    }

    [Fact]
    public async Task OnTagChange_triggers_once_for_repeated_violations()
    {
        var t = BuildWithInterlock();
        SetupRule(t);
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
        FdcInterlockResolvedEventArgs? resolved = null;
        t.sut.InterlockResolved += (_, e) => resolved = e;

        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 90.0, PlcQuality.Good));   // 발동
        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));   // 정상 복귀

        resolved.Should().NotBeNull("정상 복귀 시 해제 이벤트가 발생한다");
        resolved!.EquipmentId.Should().Be("EQ-001");
        resolved.ParameterId.Should().Be("TEMP01");
    }

    [Fact]
    public async Task OnTagChange_does_not_resolve_when_never_triggered()
    {
        var t = BuildWithInterlock();
        SetupRule(t);
        var resolvedFired = false;
        t.sut.InterlockResolved += (_, _) => resolvedFired = true;

        await t.sut.OnTagChangeAsync("EQ-001", Event("TEMP01", 50.0, PlcQuality.Good));   // 처음부터 정상

        resolvedFired.Should().BeFalse("발동한 적 없으면 해제 이벤트도 없다");
    }

    // ── 구독 연결 end-to-end (OpcUaDeviceInterface + FdcCollectorService) ──────────

    [Fact]
    public async Task StartCollecting_subscribes_and_records_on_tag_change()
    {
        // NexusLogic 연결 모킹: SubscriptionProvider.StartAsync가 onEvent 콜백을 포착하도록 설정
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
        driver.Setup(d => d.ConnectAsync(It.IsAny<PlcEndpoint>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(conn.Object);

        var device = new OpcUaDeviceInterface("EP1", endpoint, driver.Object);
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
        await collector.StartCollectingAsync(device, new[] { sub });

        onEvent.Should().NotBeNull("구독이 SubscriptionProvider에 연결된다");
        await onEvent!(new PlcTagChangeEvent("e", "EP1", "TEMP01", "ns", null, 42.0,
            PlcQuality.Good, DateTimeOffset.UnixEpoch, "polling", true));

        saved.Should().NotBeNull("구독 콜백이 수집 데이터로 적재된다");
        saved!.ParameterId.Should().Be("TEMP01");
        saved.Value.Should().Be(42m);
        saved.Quality.Should().Be("Good");
    }

    [Fact]
    public async Task StartCollecting_throws_when_device_not_initialized()
    {
        var device = new OpcUaDeviceInterface("EP1",
            new PlcEndpoint("EP1", PlcDriverKind.OpcUa, "opc.tcp://h:1"), Mock.Of<IPlcDriver>());
        var collector = new FdcCollectorService(
            new FdcDataService(Mock.Of<IFdcParameterRepository>(), Mock.Of<IFdcCollectDataRepository>()));

        var act = () => collector.StartCollectingAsync(device, Array.Empty<PlcSubscription>());

        await act.Should().ThrowAsync<InvalidOperationException>("초기화 전에는 구독을 걸 수 없다");
    }
}
