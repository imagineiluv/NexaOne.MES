using NexaOne.EPT.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class EptDomainTests
{
    private static readonly DateTime Occurred = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    // ── EquipmentAlarm ────────────────────────────────────────────────────────

    [Fact]
    public void Create_alarm_valid_succeeds()
    {
        var result = EquipmentAlarm.Create("ALM001", "EQ001", "A001", "고온 알람", "Warning", Occurred);
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
        result.Value.ClearedAt.Should().BeNull();
    }

    [Fact]
    public void Create_alarm_missing_id_fails()
    {
        var result = EquipmentAlarm.Create("", "EQ001", "A001", "알람", "Warning", Occurred);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_alarm_missing_equipment_id_fails()
    {
        var result = EquipmentAlarm.Create("ALM001", "", "A001", "알람", "Warning", Occurred);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Clear_alarm_sets_cleared_at_and_elapsed_seconds()
    {
        var alarm = EquipmentAlarm.Create("ALM001", "EQ001", "A001", "알람", "Critical", Occurred).Value;
        var clearedAt = Occurred.AddMinutes(10);

        alarm.Clear(clearedAt);

        alarm.IsActive.Should().BeFalse();
        alarm.ClearedAt.Should().Be(clearedAt);
        alarm.ElapsedSeconds.Should().Be(600);
    }

    [Fact]
    public void Alarm_is_active_before_clear_and_inactive_after()
    {
        var alarm = EquipmentAlarm.Create("ALM001", "EQ001", "A001", "알람", "Warning", Occurred).Value;
        alarm.IsActive.Should().BeTrue();

        alarm.Clear(Occurred.AddSeconds(1));
        alarm.IsActive.Should().BeFalse();
    }

    // ── EquipmentStateHistory ─────────────────────────────────────────────────

    [Fact]
    public void Create_state_history_valid_succeeds()
    {
        var result = EquipmentStateHistory.Create("HST001", "EQ001", "Idle", "Running", "Running", Occurred, "user01", "생산 시작");
        result.IsSuccess.Should().BeTrue();
        result.Value.FromState.Should().Be("Idle");
        result.Value.ToState.Should().Be("Running");
    }

    [Fact]
    public void Create_state_history_missing_id_fails()
    {
        var result = EquipmentStateHistory.Create("", "EQ001", "Idle", "Running", "Running", Occurred, "user01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SetDuration_calculates_seconds_correctly()
    {
        var history = EquipmentStateHistory.Create("HST001", "EQ001", "Idle", "Running", "Running", Occurred, "user01").Value;
        history.SetDuration(Occurred.AddHours(2));
        history.DurationSeconds.Should().Be(7200);
    }
}
