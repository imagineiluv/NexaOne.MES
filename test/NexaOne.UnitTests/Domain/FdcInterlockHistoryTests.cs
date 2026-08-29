using System.Text.Json;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>FdcInterlockHistory 애그리거트 — 발동/해제 시 도메인 이벤트 발행(ADR-002 outbox 슬라이스).</summary>
public sealed class FdcInterlockHistoryTests
{
    [Fact]
    public void Create_raises_canonical_InterlockTriggered_event_with_outbox_envelope()
    {
        var history = FdcInterlockHistory.Create(
            "IH-1", "RULE-1", "EQ-1", "PARAM-1", 42.5m, "STOP", "[FDC] 인터록 발동", DateTime.UtcNow).Value;

        var ev = history.DomainEvents.OfType<FdcInterlockTriggeredDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("InterlockTriggered");
        ev.Module.Should().Be("FDC");
        ev.AggregateId.Should().Be("EQ-1", "설비별 순서 보장을 위해 AGGREGATE_ID는 EquipmentId여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EffectId").GetString().Should().Be("IH-1");
        doc.RootElement.GetProperty("RuleId").GetString().Should().Be("RULE-1");
        doc.RootElement.GetProperty("ParameterId").GetString().Should().Be("PARAM-1");
        doc.RootElement.GetProperty("Action").GetString().Should().Be("STOP");
        doc.RootElement.GetProperty("Message").GetString().Should().Be("[FDC] 인터록 발동");
        doc.RootElement.GetProperty("Value").GetDecimal().Should().Be(42.5m);
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => FdcInterlockHistory.Restore("IH-2", "RULE-1", "EQ-1", "PARAM-1", 1m, "ALARM", "msg",
                DateTime.UtcNow, DateTime.UtcNow, true,
                effectState: FdcInterlockEffectState.Resolved,
                lastError: "LegacyResolvedBeforeV146")
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void Restore_rejects_inconsistent_terminal_state()
    {
        var restore = () => FdcInterlockHistory.Restore(
            "IH-BAD", "RULE-1", "EQ-1", "PARAM-1", 1m, "STOP", "msg",
            DateTime.UtcNow, DateTime.UtcNow, true,
            effectState: FdcInterlockEffectState.Applied);

        restore.Should().Throw<InvalidOperationException>()
            .WithMessage("*inconsistent resolved/state*");
    }

    [Fact]
    public void Lifecycle_rejects_skipping_apply_normalization_or_release_evidence()
    {
        var history = FdcInterlockHistory.Create(
            "IH-ORDER", "RULE-1", "EQ-1", "PARAM-1", 1m, "STOP", "msg", DateTime.UtcNow).Value;

        var normalizePrepared = () => history.MarkConditionNormalized(DateTime.UtcNow, 0m);
        normalizePrepared.Should().Throw<InvalidOperationException>().WithMessage("*cannot normalize from state 'Prepared'*");

        var releasePrepared = () => history.MarkReleasePending("invalid transition");
        releasePrepared.Should().Throw<InvalidOperationException>().WithMessage("*cannot await release from state 'Prepared'*");

        var resolvePrepared = () => history.Resolve(DateTime.UtcNow, 0m);
        resolvePrepared.Should().Throw<InvalidOperationException>().WithMessage("*cannot resolve from state 'Prepared'*");
    }

    [Fact]
    public void Restore_rejects_applied_state_without_acknowledgement_evidence()
    {
        var restore = () => FdcInterlockHistory.Restore(
            "IH-NO-ACK", "RULE-1", "EQ-1", "PARAM-1", 1m, "STOP", "msg",
            DateTime.UtcNow, null, false,
            effectState: FdcInterlockEffectState.Applied);

        restore.Should().Throw<InvalidOperationException>()
            .WithMessage("*no confirmed apply evidence*");
    }

    [Fact]
    public void Resolve_raises_canonical_InterlockResolved_event_with_outbox_envelope()
    {
        var triggered = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var history = FdcInterlockHistory.Create(
            "IH-3", "RULE-9", "EQ-9", "PARAM-9", 1m, "STOP", "msg", triggered).Value;
        history.ClearDomainEvents();   // 발동 이벤트 제거 — 해제 이벤트만 검증

        var resolvedAt = triggered.AddMinutes(5);
        history.MarkApplied("apply-3", triggered.AddMinutes(1));
        history.MarkConditionNormalized(triggered.AddMinutes(4), 0.5m);
        history.MarkReleaseConfirmed("release-3", resolvedAt);
        history.Resolve(resolvedAt, 0.5m);

        var ev = history.DomainEvents.OfType<FdcInterlockResolvedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("InterlockResolved");
        ev.Module.Should().Be("FDC");
        ev.AggregateId.Should().Be("EQ-9");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("EffectId").GetString().Should().Be("IH-3");
        doc.RootElement.GetProperty("RuleId").GetString().Should().Be("RULE-9");
        doc.RootElement.GetProperty("ParameterId").GetString().Should().Be("PARAM-9");
        doc.RootElement.GetProperty("Value").GetDecimal().Should().Be(0.5m);
        doc.RootElement.GetProperty("ResolvedAt").GetDateTime().Should().Be(resolvedAt);
    }

    [Fact]
    public void Resolve_when_already_resolved_raises_no_event()
    {
        var history = FdcInterlockHistory.Create(
            "IH-4", "RULE-1", "EQ-1", "PARAM-1", 1m, "STOP", "msg", DateTime.UtcNow).Value;
        var appliedAt = DateTime.UtcNow;
        var normalizedAt = appliedAt.AddSeconds(1);
        var resolvedAt = normalizedAt.AddSeconds(1);
        history.MarkApplied("apply-4", appliedAt);
        history.MarkConditionNormalized(normalizedAt, 0m);
        history.MarkReleaseConfirmed("release-4", resolvedAt);
        history.Resolve(resolvedAt);
        history.ClearDomainEvents();   // 첫 해제 이벤트 제거

        history.Resolve(DateTime.UtcNow);   // 멱등 — 전이 없음

        history.DomainEvents.Should().BeEmpty("이미 해제된 인터락의 재해제는 상태 전이가 없어 이벤트를 발행하지 않는다");
    }

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var history = FdcInterlockHistory.Create(
            "IH-5", "RULE-1", "EQ-1", "PARAM-1", 1m, "STOP", "msg", DateTime.UtcNow).Value;
        history.ClearDomainEvents();
        history.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
