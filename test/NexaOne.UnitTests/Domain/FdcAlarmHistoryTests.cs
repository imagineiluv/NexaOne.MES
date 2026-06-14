using System.Text.Json;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>FdcAlarmHistory 애그리거트 — 발생/해제 시 도메인 이벤트 발행(ADR-002 EST 알람 슬라이스의 FDC 미러).</summary>
public sealed class FdcAlarmHistoryTests
{
    [Fact]
    public void Create_raises_FdcAlarmRaised_event_with_outbox_envelope()
    {
        var alarm = FdcAlarmHistory.Create(
            "AL-1", "CFG-1", "EQ-1", "PARAM-1", "CRITICAL", 123.45m, "[FDC] 임계 초과", DateTime.UtcNow).Value;

        var ev = alarm.DomainEvents.OfType<FdcAlarmRaisedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("FdcAlarmRaised");
        ev.Module.Should().Be("FDC");
        ev.AggregateId.Should().Be("EQ-1", "설비별 순서 보장을 위해 AGGREGATE_ID는 EquipmentId여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("AlarmId").GetString().Should().Be("AL-1");
        doc.RootElement.GetProperty("AlarmConfigId").GetString().Should().Be("CFG-1");
        doc.RootElement.GetProperty("ParameterId").GetString().Should().Be("PARAM-1");
        doc.RootElement.GetProperty("AlarmLevel").GetString().Should().Be("CRITICAL");
        doc.RootElement.GetProperty("TriggerValue").GetDecimal().Should().Be(123.45m);
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => FdcAlarmHistory.Restore("AL-2", "CFG-1", "EQ-1", "PARAM-1", "WARNING", 1m, "msg",
                DateTime.UtcNow, null, false)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void Clear_raises_FdcAlarmCleared_event_with_cleared_at()
    {
        var occurred = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clearedAt = occurred.AddSeconds(90);
        var alarm = FdcAlarmHistory.Create(
            "AL-3", "CFG-9", "EQ-9", "PARAM-9", "WARNING", 5m, "msg", occurred).Value;
        alarm.ClearDomainEvents();   // 발생 이벤트 제거 — 해제 이벤트만 검증

        alarm.Clear(clearedAt);

        var ev = alarm.DomainEvents.OfType<FdcAlarmClearedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("FdcAlarmCleared");
        ev.Module.Should().Be("FDC");
        ev.AggregateId.Should().Be("EQ-9");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("AlarmId").GetString().Should().Be("AL-3");
        doc.RootElement.GetProperty("ClearedAt").GetDateTime().Should().Be(clearedAt);
    }

    [Fact]
    public void Clear_is_idempotent_and_raises_no_event_when_already_cleared()
    {
        var occurred = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var alarm = FdcAlarmHistory.Create("AL-5", "CFG-1", "EQ-1", "PARAM-1", "WARNING", 1m, "msg", occurred).Value;
        alarm.Clear(occurred.AddSeconds(10));
        alarm.ClearDomainEvents();   // 첫 해제 이벤트 제거 — 재해제가 추가 발행하지 않음을 검증

        alarm.Clear(occurred.AddSeconds(20));   // 멱등: 이미 해제됨 → no-op

        alarm.DomainEvents.OfType<FdcAlarmClearedDomainEvent>().Should().BeEmpty(
            "이미 해제된 알람의 재해제는 멱등이라 추가 이벤트를 발행하지 않아야 한다");
    }

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var alarm = FdcAlarmHistory.Create("AL-4", "CFG-1", "EQ-1", "PARAM-1", "WARNING", 1m, "msg", DateTime.UtcNow).Value;
        alarm.ClearDomainEvents();
        alarm.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
