using NexaOne.EST.Domain.Oee;

namespace NexaOne.UnitTests.Oee;

/// <summary>순수 OEE 계산기 단위검증(EST 모듈 도메인) — 상태이력 타일링(가용성)·수량(품질)·목표사이클(성능) 결합과
/// 6대 손실 집계, 경계(비계획 IDLE 제외·상태이력 부재 폴백·0나눗셈·비율 클램프·작업조 계획시간 override)를 DB 없이 검증한다.</summary>
public sealed class OeeCalculatorTests
{
    private static readonly Dictionary<string, OeeStateCategory> Cats = new(StringComparer.Ordinal)
    {
        ["RUN"] = new("Productive", IsProductive: true, IsDowntime: false, IsScheduled: true),
        ["DOWN"] = new("Breakdown", IsProductive: false, IsDowntime: true, IsScheduled: true),
        ["SETUP"] = new("Setup", IsProductive: false, IsDowntime: true, IsScheduled: true),
        ["IDLE"] = new("Idle", IsProductive: false, IsDowntime: false, IsScheduled: false),
    };
    private static readonly OeeStateCategory Unknown = new("Unknown", false, false, true);
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Computes_apq_and_oee_from_state_history_and_counts()
    {
        // 윈도 8시간(480분). RUN 360 + DOWN 30 + RUN 90 = 가동 450 / 비가동 30 / 계획 480.
        var transitions = new[]
        {
            new OeeStateTransition(T0, "IDLE", "RUN"),
            new OeeStateTransition(T0.AddHours(6), "RUN", "DOWN"),
            new OeeStateTransition(T0.AddMinutes(390), "DOWN", "RUN"), // 06:30
        };
        var result = OeeCalculator.Compute(
            T0, T0.AddHours(8), transitions,
            new OeeLotCounts(TotalQty: 900m, DefectQty: 45m),
            new OeeTarget(IdealCycleTimeSec: 30m, PlannedMinutes: 480m),
            Cats, Unknown);

        result.OperatingMinutes.Should().Be(450m);
        result.DowntimeMinutes.Should().Be(30m);
        result.PlannedMinutes.Should().Be(480m);
        result.Availability.Should().Be(0.9375m);        // 450/480
        result.Quality.Should().Be(0.95m);               // 855/900
        result.Performance.Should().Be(1.0m);            // (30×900)/(450×60)=1
        result.Oee.Should().Be(0.8906m);                 // 0.9375×1×0.95 반올림
        result.GoodCount.Should().Be(855m);
        result.Losses.Should().ContainSingle(l => l.Category == "Breakdown" && l.Minutes == 30m);
    }

    [Fact]
    public void Idle_time_is_excluded_from_planned_time()
    {
        // RUN 480 + IDLE 120. IDLE는 비계획이라 계획시간에서 제외 → 가용성 = 480/480 = 1.
        var transitions = new[]
        {
            new OeeStateTransition(T0, "IDLE", "RUN"),
            new OeeStateTransition(T0.AddHours(8), "RUN", "IDLE"),
        };
        var result = OeeCalculator.Compute(
            T0, T0.AddHours(10), transitions,
            new OeeLotCounts(1000m, 0m), new OeeTarget(30m, 600m), Cats, Unknown);

        result.PlannedMinutes.Should().Be(480m, "비계획 IDLE 120분은 계획시간에서 제외돼야 한다");
        result.OperatingMinutes.Should().Be(480m);
        result.Availability.Should().Be(1.0m);
        result.DowntimeMinutes.Should().Be(0m);
    }

    [Fact]
    public void No_state_history_falls_back_to_target_planned_minutes()
    {
        var result = OeeCalculator.Compute(
            T0, T0.AddHours(8), Array.Empty<OeeStateTransition>(),
            new OeeLotCounts(500m, 10m), new OeeTarget(30m, 480m), Cats, Unknown);

        result.PlannedMinutes.Should().Be(480m, "상태이력이 없으면 목표 계획시간으로 폴백");
        result.OperatingMinutes.Should().Be(0m);
        result.Availability.Should().Be(0m, "가동 상태가 없으면 가용성 0");
        result.Oee.Should().Be(0m, "가용성 0 → OEE 0");
    }

    [Fact]
    public void Zero_production_yields_zero_quality_and_performance_without_throwing()
    {
        var transitions = new[] { new OeeStateTransition(T0, "IDLE", "RUN") };
        var result = OeeCalculator.Compute(
            T0, T0.AddHours(8), transitions,
            new OeeLotCounts(0m, 0m), new OeeTarget(30m, 480m), Cats, Unknown);

        result.Quality.Should().Be(0m, "총 생산 0 → 품질 0(0나눗셈 회피)");
        result.Performance.Should().Be(0m, "총 생산 0 → 성능 0");
        result.Oee.Should().Be(0m);
    }

    [Fact]
    public void Performance_is_clamped_to_one_when_actual_faster_than_ideal()
    {
        // 가동 100분에 이상 30초/개 기준 1000개면 이론상 성능 = 500/100 = 5.0 → 1.0으로 클램프.
        var transitions = new[]
        {
            new OeeStateTransition(T0, "IDLE", "RUN"),
            new OeeStateTransition(T0.AddMinutes(100), "RUN", "IDLE"),
        };
        var result = OeeCalculator.Compute(
            T0, T0.AddHours(4), transitions,
            new OeeLotCounts(1000m, 0m), new OeeTarget(30m, 240m), Cats, Unknown);

        result.Performance.Should().Be(1.0m, "성능은 [0,1]로 클램프돼 OEE≤1을 보장한다");
    }

    [Fact]
    public void Planned_override_takes_precedence_over_derived_scheduled_time()
    {
        // RUN 480(파생 계획 480). 그러나 작업조 override 720(12h)이 우선 → 가용성 = 480/720.
        var transitions = new[]
        {
            new OeeStateTransition(T0, "IDLE", "RUN"),
            new OeeStateTransition(T0.AddHours(8), "RUN", "IDLE"),
        };
        var result = OeeCalculator.Compute(
            T0, T0.AddHours(12), transitions,
            new OeeLotCounts(1000m, 0m), new OeeTarget(30m, 480m), Cats, Unknown,
            plannedOverride: 720m);

        result.PlannedMinutes.Should().Be(720m, "작업조/근무달력 계획시간 override가 파생값보다 우선");
        result.OperatingMinutes.Should().Be(480m);
        result.Availability.Should().Be(0.6667m, "480/720 반올림");
    }
}
