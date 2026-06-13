using System.Diagnostics.Metrics;
using NexaOne.Common.Telemetry;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexusLogic.Plc.Abstractions.Models;

namespace NexaOne.UnitTests.Telemetry;

/// <summary>§17.5 비즈니스 Metrics — 활동 사용자/설비 채널 트래커 로직과, "NexaMes" Meter의
/// 항시 계측(api_request_duration_ms·api_error_rate·fdc_collected_total)이 실제로 방출되는지 검증한다.
/// MeterListener는 프로세스 전역 Meter를 관측하므로, 측정은 고유 태그로 필터해 병렬 테스트와 격리한다.</summary>
public sealed class NexaMesTelemetryTests
{
    // ── ActiveUserTracker (nexames_active_users) ────────────────────────────────

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static Dictionary<string, long> ByPlant(IEnumerable<Measurement<long>> ms)
        => ms.ToDictionary(
            m => m.Tags.ToArray().First(t => t.Key == "plantId").Value!.ToString()!,
            m => m.Value);

    [Fact]
    public void ActiveUsers_counts_distinct_users_per_plant()
    {
        var tracker = new ActiveUserTracker(new FakeClock(DateTimeOffset.UnixEpoch));
        tracker.Touch("P1", "alice");
        tracker.Touch("P1", "bob");
        tracker.Touch("P1", "alice");   // 중복 → 1명으로 집계
        tracker.Touch("P2", "carol");

        var byPlant = ByPlant(tracker.Observe());

        byPlant["P1"].Should().Be(2, "고유 사용자만 집계한다");
        byPlant["P2"].Should().Be(1);
    }

    [Fact]
    public void ActiveUsers_expire_outside_the_window()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var tracker = new ActiveUserTracker(clock, TimeSpan.FromMinutes(5));
        tracker.Touch("P1", "alice");

        clock.Advance(TimeSpan.FromMinutes(6));   // 윈도(5분) 초과

        tracker.Observe().Should().BeEmpty("윈도를 벗어난 활동은 집계되지 않는다");
    }

    [Fact]
    public void ActiveUsers_keep_recent_within_window()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var tracker = new ActiveUserTracker(clock, TimeSpan.FromMinutes(5));
        tracker.Touch("P1", "alice");

        clock.Advance(TimeSpan.FromMinutes(4));
        tracker.Touch("P1", "bob");

        ByPlant(tracker.Observe())["P1"].Should().Be(2, "윈도 내 활동은 모두 집계된다");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anonymous")]
    public void ActiveUsers_ignore_anonymous_and_empty(string? userId)
    {
        var tracker = new ActiveUserTracker(new FakeClock(DateTimeOffset.UnixEpoch));
        tracker.Touch("P1", userId);

        tracker.Observe().Should().BeEmpty("익명/빈 사용자는 활동으로 집계하지 않는다");
    }

    // ── EquipmentChannelStatusRegistry (nexames_equipment_channel_status) ────────

    private static Dictionary<string, int> ByEquipment(IEnumerable<Measurement<int>> ms)
        => ms.ToDictionary(
            m => m.Tags.ToArray().First(t => t.Key == "equipmentId").Value!.ToString()!,
            m => m.Value);

    [Fact]
    public void ChannelStatus_reflects_up_and_down()
    {
        var reg = new EquipmentChannelStatusRegistry();
        reg.Set("EQ-1", "OpcUa", up: true);
        reg.Set("EQ-2", "OpcUa", up: false);

        var byEq = ByEquipment(reg.Observe());

        byEq["EQ-1"].Should().Be(1, "정상 채널은 1");
        byEq["EQ-2"].Should().Be(0, "비정상 채널은 0");
    }

    [Fact]
    public void ChannelStatus_set_false_overwrites_previous_up()
    {
        var reg = new EquipmentChannelStatusRegistry();
        reg.Set("EQ-1", "OpcUa", up: true);
        reg.Set("EQ-1", "OpcUa", up: false);   // 동일 키 갱신

        ByEquipment(reg.Observe())["EQ-1"].Should().Be(0);
    }

    [Fact]
    public void ChannelStatus_remove_drops_the_entry()
    {
        var reg = new EquipmentChannelStatusRegistry();
        reg.Set("EQ-1", "OpcUa", up: true);
        reg.Remove("EQ-1", "OpcUa");

        reg.Observe().Should().BeEmpty("제거된 채널은 관측되지 않는다");
    }

    // ── 정적 계측 방출 (MeterListener) ──────────────────────────────────────────

    /// <summary>"NexaMes" Meter의 지정 instrument 측정을 수집한다(고유 태그로 필터). act 실행 동안 리스너가 활성.</summary>
    private static async Task<List<(double Value, Dictionary<string, object?> Tags)>> CollectAsync(
        string instrumentName, Func<Task> act)
    {
        // 정적 instrument가 생성되도록 타입 초기화를 강제
        _ = NexaMesMetrics.ApiRequestDuration;
        _ = NexaMesMetrics.ApiErrors;
        _ = NexaMesMetrics.FdcCollected;

        var captured = new List<(double, Dictionary<string, object?>)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == NexaMesMetrics.MeterName && inst.Name == instrumentName)
                    l.EnableMeasurementEvents(inst);
            }
        };
        void Record<T>(Instrument inst, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _)
            where T : struct
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            captured.Add((Convert.ToDouble(value), d));
        }
        listener.SetMeasurementEventCallback<double>((i, v, t, s) => Record(i, v, t, s));
        listener.SetMeasurementEventCallback<long>((i, v, t, s) => Record(i, v, t, s));
        listener.Start();

        await act();

        return captured;
    }

    [Fact]
    public async Task ApiRequestDuration_records_value_and_labels()
    {
        var route = $"/__test__/{Guid.NewGuid():N}";
        var captured = await CollectAsync("nexames_api_request_duration_ms", () =>
        {
            var tags = new System.Diagnostics.TagList
            {
                { "route", route }, { "method", "GET" }, { "plantId", "P1" }, { "resultCode", 200 },
            };
            NexaMesMetrics.ApiRequestDuration.Record(12.5, tags);
            return Task.CompletedTask;
        });

        var mine = captured.Where(c => Equals(c.Tags["route"], route)).ToList();
        mine.Should().ContainSingle();
        mine[0].Value.Should().Be(12.5);
        mine[0].Tags["method"].Should().Be("GET");
        mine[0].Tags["plantId"].Should().Be("P1");
        mine[0].Tags["resultCode"].Should().Be(200);
    }

    [Fact]
    public async Task ApiErrors_increments_with_error_labels()
    {
        var route = $"/__test__/{Guid.NewGuid():N}";
        var captured = await CollectAsync("nexames_api_error_rate", () =>
        {
            var tags = new System.Diagnostics.TagList
            {
                { "route", route }, { "errorCode", 500 }, { "plantId", "P1" },
            };
            NexaMesMetrics.ApiErrors.Add(1, tags);
            return Task.CompletedTask;
        });

        var mine = captured.Where(c => Equals(c.Tags["route"], route)).ToList();
        mine.Should().ContainSingle();
        mine[0].Value.Should().Be(1);
        mine[0].Tags["errorCode"].Should().Be(500);
    }

    [Fact]
    public async Task FdcCollected_increments_on_successful_record()
    {
        const string equipmentId = "EQ-METRIC-TEST";   // 병렬 FDC 테스트("EQ-001")와 격리
        var param = FdcParameter.Create("TEMP01", "Temp", equipmentId, "C", 0m, 100m).Value;
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("TEMP01", It.IsAny<CancellationToken>())).ReturnsAsync(param);
        var dataRepo = new Mock<IFdcCollectDataRepository>();
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        var collector = new FdcCollectorService(new FdcDataService(paramRepo.Object, dataRepo.Object));

        var evt = new PlcTagChangeEvent("e", equipmentId, "TEMP01", "ns", null, 42.0,
            PlcQuality.Good, DateTimeOffset.UnixEpoch, "polling", true);

        var captured = await CollectAsync("nexames_fdc_collected_total",
            () => collector.OnTagChangeAsync(equipmentId, evt));

        var mine = captured.Where(c => Equals(c.Tags["equipmentId"], equipmentId)).ToList();
        mine.Should().ContainSingle("정의된 파라미터의 적재 성공 1건이 계측된다");
        mine[0].Value.Should().Be(1);
        mine[0].Tags["quality"].Should().Be("Good");
    }

    [Fact]
    public async Task FdcCollected_not_incremented_for_unknown_parameter()
    {
        const string equipmentId = "EQ-METRIC-NONE";
        var paramRepo = new Mock<IFdcParameterRepository>();
        paramRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((FdcParameter?)null);
        var dataRepo = new Mock<IFdcCollectDataRepository>();
        var collector = new FdcCollectorService(new FdcDataService(paramRepo.Object, dataRepo.Object));

        var evt = new PlcTagChangeEvent("e", equipmentId, "UNKNOWN", "ns", null, 1.0,
            PlcQuality.Good, DateTimeOffset.UnixEpoch, "polling", true);

        var captured = await CollectAsync("nexames_fdc_collected_total",
            () => collector.OnTagChangeAsync(equipmentId, evt));

        captured.Where(c => Equals(c.Tags["equipmentId"], equipmentId))
                .Should().BeEmpty("적재 실패(미정의 파라미터)는 계측하지 않는다");
    }
}
