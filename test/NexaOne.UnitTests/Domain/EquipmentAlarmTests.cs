using System.Text.Json;
using NexaOne.EST.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>EquipmentAlarm 애그리거트 — 발생/해제 시 도메인 이벤트 발행(ADR-002 상태 슬라이스의 알람 확장).</summary>
public sealed class EquipmentAlarmTests
{
    [Fact]
    public void Create_raises_EquipmentAlarmRaised_event_with_outbox_envelope()
    {
        var alarm = EquipmentAlarm.Create("AL-1", "EQ-1", "OVERTEMP", "[FDC] 과열", "CRITICAL", DateTime.UtcNow).Value;

        var ev = alarm.DomainEvents.OfType<EquipmentAlarmRaisedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("EquipmentAlarmRaised");
        ev.Module.Should().Be("EST");
        ev.AggregateId.Should().Be("EQ-1", "설비별 순서 보장을 위해 AGGREGATE_ID는 EquipmentId여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("AlarmId").GetString().Should().Be("AL-1");
        doc.RootElement.GetProperty("AlarmCode").GetString().Should().Be("OVERTEMP");
        doc.RootElement.GetProperty("AlarmLevel").GetString().Should().Be("CRITICAL");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => EquipmentAlarm.Restore("AL-2", "EQ-1", "C", "N", "WARNING", DateTime.UtcNow, null, null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void Clear_raises_EquipmentAlarmCleared_event_with_elapsed_seconds()
    {
        var occurred = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var alarm = EquipmentAlarm.Create("AL-3", "EQ-9", "C", "N", "WARNING", occurred).Value;
        alarm.ClearDomainEvents();   // 발생 이벤트 제거 — 해제 이벤트만 검증

        alarm.Clear(occurred.AddSeconds(90));

        var ev = alarm.DomainEvents.OfType<EquipmentAlarmClearedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("EquipmentAlarmCleared");
        ev.Module.Should().Be("EST");
        ev.AggregateId.Should().Be("EQ-9");
        ev.ElapsedSeconds.Should().Be(90);

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("AlarmId").GetString().Should().Be("AL-3");
        doc.RootElement.GetProperty("ElapsedSeconds").GetInt64().Should().Be(90);
    }

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var alarm = EquipmentAlarm.Create("AL-4", "EQ-1", "C", "N", "WARNING", DateTime.UtcNow).Value;
        alarm.ClearDomainEvents();
        alarm.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
