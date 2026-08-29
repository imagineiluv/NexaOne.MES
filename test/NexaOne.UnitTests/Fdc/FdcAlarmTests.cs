using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>FDC 임계치 알람(FDC_ALARM_CONFIG/HISTORY)의 도메인 검증·평가·이력 기록/해제를 검증한다.</summary>
public sealed class FdcAlarmTests
{
    private static readonly DateTime At = new(2026, 6, 13, 5, 0, 0, DateTimeKind.Utc);

    // ── Config 도메인 ──

    [Theory]
    [InlineData("Warning")]
    [InlineData("Critical")]
    [InlineData("critical")]   // 대소문자 무관 → 정규화
    public void Config_create_accepts_valid_levels(string level)
        => FdcAlarmConfig.Create("A1", "EQ-001", "TEMP01", level, "GT", 80m).IsFailure.Should().BeFalse();

    [Theory]
    [InlineData("Fatal", "GT")]    // 미지원 레벨
    [InlineData("Warning", "NE")]  // 미지원 연산자
    public void Config_create_rejects_invalid(string level, string op)
        => FdcAlarmConfig.Create("A1", "EQ-001", "TEMP01", level, op, 80m).IsFailure.Should().BeTrue();

    [Fact]
    public void Config_normalizes_level_casing()
        => FdcAlarmConfig.Create("A1", "EQ-001", "TEMP01", "critical", "GT", 80m).Value.AlarmLevel.Should().Be("Critical");

    [Theory]
    [InlineData("GT", 90, true)]
    [InlineData("GT", 70, false)]
    [InlineData("LTE", 80, true)]
    [InlineData("EQ", 80, true)]
    public void Config_evaluate_applies_operator(string op, int value, bool expected)
        => FdcAlarmConfig.Create("A1", "EQ-001", "TEMP01", "Warning", op, 80m).Value.Evaluate(value).Should().Be(expected);

    // ── History 도메인 ──

    [Fact]
    public void History_create_and_clear_is_idempotent()
    {
        var h = FdcAlarmHistory.Create("AH1", "A1", "EQ-001", "TEMP01", "Critical", 95m, "msg", At).Value;
        h.IsCleared.Should().BeFalse();

        h.Clear(At.AddMinutes(3));
        h.IsCleared.Should().BeTrue();
        h.ClearedAt.Should().Be(At.AddMinutes(3));

        h.Clear(At.AddMinutes(9));   // 멱등
        h.ClearedAt.Should().Be(At.AddMinutes(3));
    }

    // ── Service ──

    [Fact]
    public async Task EvaluateAsync_returns_all_matching_configs()
    {
        var warn = FdcAlarmConfig.Create("AW", "EQ-001", "TEMP01", "Warning", "GT", 70m).Value;
        var crit = FdcAlarmConfig.Create("AC", "EQ-001", "TEMP01", "Critical", "GT", 90m).Value;
        var cfgRepo = new Mock<IFdcAlarmConfigRepository>();
        cfgRepo.Setup(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { warn, crit });
        var svc = new FdcAlarmService(cfgRepo.Object);

        var hits = await svc.EvaluateAsync("EQ-001", "TEMP01", 95m);

        hits.Select(h => h.AlarmLevel).Should().BeEquivalentTo(new[] { "Warning", "Critical" },
            "95는 Warning(>70)·Critical(>90) 양쪽을 발동한다");
    }

    [Fact]
    public async Task RecordAsync_persists_alarm_history()
    {
        FdcAlarmHistory? saved = null;
        var histRepo = new Mock<IFdcAlarmHistoryRepository>();
        histRepo.Setup(r => r.AddAsync(It.IsAny<FdcAlarmHistory>(), It.IsAny<CancellationToken>()))
                .Callback<FdcAlarmHistory, CancellationToken>((h, _) => saved = h)
                .Returns(Task.CompletedTask);
        var svc = new FdcAlarmService(Mock.Of<IFdcAlarmConfigRepository>(), histRepo.Object);

        var result = await svc.RecordAsync("EQ-001", "TEMP01", 95m,
            new AlarmResult("AC", "Critical", "over"));

        result.IsFailure.Should().BeFalse();
        saved!.AlarmConfigId.Should().Be("AC");
        saved.AlarmLevel.Should().Be("Critical");
        saved.TriggerValue.Should().Be(95m);
    }

    [Fact]
    public async Task ClearActiveAsync_clears_only_matching_parameter()
    {
        var a1 = FdcAlarmHistory.Create("AH1", "AC", "EQ-001", "TEMP01", "Critical", 95m, "m", At).Value;
        var a2 = FdcAlarmHistory.Create("AH2", "AW", "EQ-001", "PRESS01", "Warning", 9m, "m", At).Value;
        var histRepo = new Mock<IFdcAlarmHistoryRepository>();
        histRepo.Setup(r => r.GetOpenAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { a1 });
        var svc = new FdcAlarmService(Mock.Of<IFdcAlarmConfigRepository>(), histRepo.Object);

        var count = await svc.ClearActiveAsync("EQ-001", "TEMP01");

        count.Should().Be(1);
        a1.IsCleared.Should().BeTrue();
        a2.IsCleared.Should().BeFalse("다른 파라미터 알람은 유지된다");
    }

    [Fact]
    public async Task ClearActiveAsync_with_config_id_clears_only_that_alarm_episode()
    {
        var warning = FdcAlarmHistory.Create(
            "AH-W", "AW", "EQ-001", "TEMP01", "Warning", 80m, "warning", At).Value;
        var critical = FdcAlarmHistory.Create(
            "AH-C", "AC", "EQ-001", "TEMP01", "Critical", 95m, "critical", At).Value;
        var repository = new Mock<IFdcAlarmHistoryRepository>();
        repository.Setup(r => r.GetOpenAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { warning, critical });
        var service = new FdcAlarmService(
            Mock.Of<IFdcAlarmConfigRepository>(), repository.Object);

        var count = await service.ClearActiveAsync("EQ-001", "TEMP01", "AC");

        count.Should().Be(1);
        critical.IsCleared.Should().BeTrue();
        warning.IsCleared.Should().BeFalse("the still-matching warning config owns a separate episode");
        repository.Verify(r => r.UpdateAsync(
            critical, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.UpdateAsync(
            warning, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetHighestOpenLevelAsync_returns_the_durable_maximum_severity()
    {
        var warning = FdcAlarmHistory.Create(
            "AH1", "AW", "EQ-001", "TEMP01", "Warning", 80m, "m", At).Value;
        var critical = FdcAlarmHistory.Create(
            "AH2", "AC", "EQ-001", "TEMP01", "Critical", 95m, "m", At).Value;
        var repository = new Mock<IFdcAlarmHistoryRepository>();
        repository.Setup(r => r.GetOpenAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { warning, critical });
        var service = new FdcAlarmService(
            Mock.Of<IFdcAlarmConfigRepository>(), repository.Object);

        (await service.GetHighestOpenLevelAsync("EQ-001", "TEMP01"))
            .Should().Be("Critical");
    }
}
