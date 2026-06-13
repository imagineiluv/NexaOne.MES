using Moq;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class EstServiceTests
{
    private static readonly DateTime Occurred = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    private static EquipmentAlarm ActiveAlarm(string id = "ALM001") =>
        EquipmentAlarm.Create(id, "EQ001", "A001", "고온 알람", "Warning", Occurred).Value;

    private EquipmentAlarmService BuildService(Mock<IEquipmentAlarmRepository> repo) =>
        new(repo.Object);

    // ── RecordAlarmAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAlarm_valid_data_succeeds()
    {
        var repo = new Mock<IEquipmentAlarmRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<EquipmentAlarm>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).RecordAlarmAsync("ALM001", "EQ001", "A001", "고온 알람", "Warning");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<EquipmentAlarm>(), default), Times.Once);
    }

    [Fact]
    public async Task RecordAlarm_missing_equipment_id_fails()
    {
        var repo = new Mock<IEquipmentAlarmRepository>();

        var result = await BuildService(repo).RecordAlarmAsync("ALM001", "", "A001", "알람", "Warning");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<EquipmentAlarm>(), default), Times.Never);
    }

    // ── ClearAlarmAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ClearAlarm_sets_cleared_at_and_elapsed()
    {
        var alarm = ActiveAlarm();
        var clearedAt = Occurred.AddMinutes(30);
        var repo = new Mock<IEquipmentAlarmRepository>();
        repo.Setup(r => r.GetByIdAsync("ALM001", default)).ReturnsAsync(alarm);
        repo.Setup(r => r.UpdateAsync(alarm, default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).ClearAlarmAsync("ALM001", clearedAt);

        result.IsSuccess.Should().BeTrue();
        alarm.IsActive.Should().BeFalse();
        alarm.ElapsedSeconds.Should().Be(1800);
        repo.Verify(r => r.UpdateAsync(alarm, default), Times.Once);
    }

    [Fact]
    public async Task ClearAlarm_not_found_returns_failure()
    {
        var repo = new Mock<IEquipmentAlarmRepository>();
        repo.Setup(r => r.GetByIdAsync("ALM999", default)).ReturnsAsync((EquipmentAlarm?)null);

        var result = await BuildService(repo).ClearAlarmAsync("ALM999", DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
    }

    // ── GetActiveAlarmsAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveAlarms_returns_list()
    {
        var alarms = new List<EquipmentAlarm> { ActiveAlarm("ALM001"), ActiveAlarm("ALM002") };
        var repo = new Mock<IEquipmentAlarmRepository>();
        repo.Setup(r => r.GetActiveAlarmsAsync("PLANT01", default))
            .ReturnsAsync((IReadOnlyList<EquipmentAlarm>)alarms);

        var result = await BuildService(repo).GetActiveAlarmsAsync("PLANT01");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetActiveAlarmCount_returns_count()
    {
        var repo = new Mock<IEquipmentAlarmRepository>();
        repo.Setup(r => r.GetActiveAlarmCountAsync(default)).ReturnsAsync(5);

        var count = await BuildService(repo).GetActiveAlarmCountAsync();

        count.Should().Be(5);
    }
}
