using NexaOne.Common;
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
    public async Task RecordTriggerAsync_retry_converges_after_ambiguous_commit()
    {
        FdcInterlockHistory? durable = null;
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetByIdAsync(
                "FX-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable);
        historyRepository.Setup(repository => repository.AddAsync(
                It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
            .Callback<FdcInterlockHistory, CancellationToken>((history, _) => durable = history)
            .ThrowsAsync(new InvalidOperationException("response lost after commit"));
        var service = new FdcInterlockService(
            Mock.Of<IFdcInterlockRuleRepository>(), historyRepository.Object);
        var result = InterlockResult.Triggered("STOP", "over temp", "R1");

        var first = () => service.RecordTriggerAsync(
            "FX-1", "EQ-001", "TEMP01", 90m, result, At);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var replay = await service.RecordTriggerAsync(
            "FX-1", "EQ-001", "TEMP01", 90m, result, At);

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeSameAs(durable);
        historyRepository.Verify(repository => repository.AddAsync(
            It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()), Times.Once,
            "ambiguous commit 재시도는 durable EffectId를 읽어 PK 충돌 없이 수렴해야 한다");
    }

    [Fact]
    public async Task RecordTriggerAsync_does_not_treat_a_different_existing_effect_as_success()
    {
        var existing = FdcInterlockHistory.Create(
            "FX-1", "OTHER-RULE", "EQ-001", "TEMP01", 90m, "STOP", "other", At).Value;
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetByIdAsync(
                "FX-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var service = new FdcInterlockService(
            Mock.Of<IFdcInterlockRuleRepository>(), historyRepository.Object);

        var result = await service.RecordTriggerAsync(
            "FX-1", "EQ-001", "TEMP01", 90m,
            InterlockResult.Triggered("STOP", "over temp", "R1"), At);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Conflict);
        historyRepository.Verify(repository => repository.AddAsync(
            It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()), Times.Never);
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

    [Fact]
    public async Task ResolveActiveAsync_resolves_only_matching_parameter_unresolved_history()
    {
        var h1 = FdcInterlockHistory.Create("H1", "R1", "EQ-001", "TEMP01", 90m, "STOP", "m", At).Value;
        var h2 = FdcInterlockHistory.Create("H2", "R2", "EQ-001", "PRESS01", 9m, "ALARM", "m", At).Value;
        var histRepo = new Mock<IFdcInterlockHistoryRepository>();
        histRepo.Setup(r => r.GetUnresolvedAsync(
                    "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { h1 });
        var updated = new List<FdcInterlockHistory>();
        histRepo.Setup(r => r.UpdateAsync(It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()))
                .Callback<FdcInterlockHistory, CancellationToken>((h, _) => updated.Add(h))
                .Returns(Task.CompletedTask);
        var svc = new FdcInterlockService(Mock.Of<IFdcInterlockRuleRepository>(), histRepo.Object);

        var count = await svc.ResolveActiveAsync("EQ-001", "TEMP01");

        count.Should().Be(1, "TEMP01 미해제 이력 1건만 해제된다");
        updated.Should().ContainSingle().Which.Id.Should().Be("H1");
        h1.IsResolved.Should().BeTrue();
        h2.IsResolved.Should().BeFalse("다른 파라미터(PRESS01) 이력은 그대로 둔다");
    }

    [Fact]
    public async Task ResolveEffectAsync_retry_converges_after_ambiguous_commit()
    {
        var durable = FdcInterlockHistory.Create(
            "FX-1", "R1", "EQ-001", "TEMP01", 90m, "STOP", "m", At).Value;
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetByIdAsync(
                "FX-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => durable);
        historyRepository.Setup(repository => repository.UpdateAsync(
                durable, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("response lost after commit"));
        var service = new FdcInterlockService(
            Mock.Of<IFdcInterlockRuleRepository>(), historyRepository.Object);
        var resolvedAt = At.AddMinutes(1);

        var first = () => service.ResolveEffectAsync(
            "FX-1", "EQ-001", "TEMP01", 50m, resolvedAt);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var replay = await service.ResolveEffectAsync(
            "FX-1", "EQ-001", "TEMP01", 50m, resolvedAt);

        replay.Should().Be(1, "durable row is already resolved despite the lost response");
        durable.IsResolved.Should().BeTrue();
        historyRepository.Verify(repository => repository.UpdateAsync(
            durable, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveEffectAsync_returns_zero_when_effect_is_not_visible()
    {
        var historyRepository = new Mock<IFdcInterlockHistoryRepository>();
        historyRepository.Setup(repository => repository.GetByIdAsync(
                "MISSING", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FdcInterlockHistory?)null);
        var service = new FdcInterlockService(
            Mock.Of<IFdcInterlockRuleRepository>(), historyRepository.Object);

        var result = await service.ResolveEffectAsync(
            "MISSING", "EQ-001", "TEMP01", 50m, At);

        result.Should().Be(0, "collector must keep the pending trigger→resolve evidence");
        historyRepository.Verify(repository => repository.UpdateAsync(
            It.IsAny<FdcInterlockHistory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HasUnresolvedAsync_reads_the_parameter_scoped_durable_state()
    {
        var history = FdcInterlockHistory.Create(
            "H1", "R1", "EQ-001", "TEMP01", 90m, "STOP", "m", At).Value;
        var repository = new Mock<IFdcInterlockHistoryRepository>();
        repository.Setup(r => r.GetUnresolvedAsync(
                "EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { history });
        var service = new FdcInterlockService(
            Mock.Of<IFdcInterlockRuleRepository>(), repository.Object);

        (await service.HasUnresolvedAsync("EQ-001", "TEMP01")).Should().BeTrue();
    }

    [Fact]
    public async Task ResolveActiveAsync_is_noop_without_history_repository()
        => (await new FdcInterlockService(Mock.Of<IFdcInterlockRuleRepository>())
                .ResolveActiveAsync("EQ-001", "TEMP01"))
            .Should().Be(0);
}
