using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>인터락 이력 도메인(FDC_INTERLOCK_HISTORY)과 발동 기록 서비스(RecordTriggerAsync)를 검증한다.</summary>
public sealed class FdcInterlockHistoryTests
{
    private static readonly DateTime At = new(2026, 6, 13, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_succeeds_with_valid_fields()
    {
        var result = FdcInterlockHistory.Create("H1", "R1", "EQ-001", "TEMP01", 90m, "STOP", "over temp", At);

        result.IsFailure.Should().BeFalse();
        var h = result.Value;
        h.RuleId.Should().Be("R1");
        h.EquipmentId.Should().Be("EQ-001");
        h.TriggerValue.Should().Be(90m);
        h.Action.Should().Be("STOP");
        h.IsResolved.Should().BeFalse("발동 직후에는 미해제 상태다");
        h.ResolvedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("", "R1", "EQ-001", "TEMP01")]
    [InlineData("H1", "", "EQ-001", "TEMP01")]
    [InlineData("H1", "R1", "", "TEMP01")]
    [InlineData("H1", "R1", "EQ-001", "")]
    public void Create_fails_when_required_field_missing(string id, string rule, string eq, string param)
    {
        FdcInterlockHistory.Create(id, rule, eq, param, 1m, "STOP", "msg", At)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Resolve_sets_resolved_state_and_is_idempotent()
    {
        var h = FdcInterlockHistory.Create("H1", "R1", "EQ-001", "TEMP01", 90m, "STOP", "msg", At).Value;
        var resolvedAt = At.AddMinutes(5);

        h.Resolve(resolvedAt);
        h.IsResolved.Should().BeTrue();
        h.ResolvedAt.Should().Be(resolvedAt);

        h.Resolve(At.AddMinutes(99));   // 멱등 — 최초 해제 시각 유지
        h.ResolvedAt.Should().Be(resolvedAt);
    }

    [Fact]
    public async Task RecordTriggerAsync_persists_history_for_triggered_result()
    {
        FdcInterlockHistory? saved = null;
        var histRepo = new Mock<IFdcInterlockHistoryRepository>();
        histRepo.Setup(r => r.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
                .Callback<FdcInterlockHistory, CancellationToken>((h, _) => saved = h)
                .Returns(Task.CompletedTask);
        var svc = new FdcInterlockService(Mock.Of<IFdcInterlockRuleRepository>(), histRepo.Object);

        var result = await svc.RecordTriggerAsync(
            "EQ-001", "TEMP01", 90m, InterlockResult.Triggered("STOP", "over temp", "R1"));

        result.IsFailure.Should().BeFalse();
        saved.Should().NotBeNull();
        saved!.RuleId.Should().Be("R1");
        saved.EquipmentId.Should().Be("EQ-001");
        saved.ParameterId.Should().Be("TEMP01");
        saved.TriggerValue.Should().Be(90m);
        saved.Action.Should().Be("STOP");
    }

    [Fact]
    public async Task RecordTriggerAsync_is_noop_without_history_repository()
    {
        var svc = new FdcInterlockService(Mock.Of<IFdcInterlockRuleRepository>());   // 이력 리포 미주입

        var result = await svc.RecordTriggerAsync(
            "EQ-001", "TEMP01", 90m, InterlockResult.Triggered("STOP", "over", "R1"));

        result.IsFailure.Should().BeTrue("이력 리포지토리가 없으면 기록하지 않는다");
    }

    [Fact]
    public async Task RecordTriggerAsync_rejects_non_triggered_result()
    {
        var histRepo = new Mock<IFdcInterlockHistoryRepository>();
        var svc = new FdcInterlockService(Mock.Of<IFdcInterlockRuleRepository>(), histRepo.Object);

        var result = await svc.RecordTriggerAsync("EQ-001", "TEMP01", 10m, InterlockResult.Pass());

        result.IsFailure.Should().BeTrue("미발동 결과는 이력으로 기록하지 않는다");
        histRepo.Verify(r => r.AddAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
