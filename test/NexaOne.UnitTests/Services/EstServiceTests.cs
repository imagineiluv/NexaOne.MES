using Moq;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Common;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.UnitTests.Services;

public sealed class EstServiceTests
{
    private static readonly DateTime Occurred = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);

    private static EquipmentAlarm ActiveAlarm(string id = "ALM001") =>
        EquipmentAlarm.Create(id, "EQ001", "A001", "고온 알람", "Warning", Occurred).Value;

    private EquipmentAlarmService BuildService(Mock<IEquipmentAlarmRepository> repo) =>
        new(repo.Object, new Mock<IEquipmentDirectory>().Object);

    private EquipmentAlarmService BuildService(Mock<IEquipmentAlarmRepository> repo, Mock<IEquipmentDirectory> directory) =>
        new(repo.Object, directory.Object);

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
    // ADR-006: 서비스가 plantId를 호스트 IEquipmentDirectory로 풀어 설비 ID를 얻고, 그 ID들로 by-ids 리포지토리를
    // 호출한다. plantId 기반 리포지토리 read는 더 이상 존재하지 않는다(MDM 조인 제거 — 교차 모듈 결합 회귀 차단).

    [Fact]
    public async Task GetActiveAlarms_resolves_equipment_ids_via_directory_then_queries_by_ids()
    {
        var ids = new[] { "EQ001", "EQ002" };
        var alarms = new List<EquipmentAlarm> { ActiveAlarm("ALM001"), ActiveAlarm("ALM002") };

        var directory = new Mock<IEquipmentDirectory>();
        directory.Setup(d => d.GetEquipmentIdsByPlantAsync("PLANT01", default))
            .ReturnsAsync((IReadOnlyList<string>)ids);

        var repo = new Mock<IEquipmentAlarmRepository>();
        repo.Setup(r => r.GetActiveAlarmsByEquipmentIdsAsync(It.Is<IReadOnlyList<string>>(x => x.SequenceEqual(ids)), default))
            .ReturnsAsync((IReadOnlyList<EquipmentAlarm>)alarms);

        var result = await BuildService(repo, directory).GetActiveAlarmsAsync("PLANT01");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        // 결합 가드: 디렉터리 포트(호스트 MDM read)와 by-ids 리포지토리만 사용. plantId 기반 read는 인터페이스에 없다.
        directory.Verify(d => d.GetEquipmentIdsByPlantAsync("PLANT01", default), Times.Once);
        repo.Verify(r => r.GetActiveAlarmsByEquipmentIdsAsync(It.IsAny<IReadOnlyList<string>>(), default), Times.Once);
    }

    [Fact]
    public async Task GetActiveAlarms_empty_plant_short_circuits_and_skips_repo()
    {
        var directory = new Mock<IEquipmentDirectory>();
        directory.Setup(d => d.GetEquipmentIdsByPlantAsync("EMPTY", default))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());
        var repo = new Mock<IEquipmentAlarmRepository>();

        var result = await BuildService(repo, directory).GetActiveAlarmsAsync("EMPTY");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        // 빈 ID 목록은 IN @...에 넘기면 안 된다 — 리포지토리는 호출되지 않아야 한다.
        repo.Verify(r => r.GetActiveAlarmsByEquipmentIdsAsync(It.IsAny<IReadOnlyList<string>>(), default), Times.Never);
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
