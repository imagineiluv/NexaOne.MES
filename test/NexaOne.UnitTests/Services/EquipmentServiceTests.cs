using Moq;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class EquipmentServiceTests
{
    private static Equipment MakeEq(string id = "EQ001") =>
        Equipment.Create(id, "Furnace #1", "P01", "A01", "Furnace").Value;

    private EquipmentService BuildService(Mock<IEquipmentRepository> repo) => new(repo.Object);

    // ── CreateEquipmentAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateEquipment_new_id_succeeds()
    {
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.ExistsAsync("EQ001", default)).ReturnsAsync(false);
        repo.Setup(r => r.AddAsync(It.IsAny<Equipment>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateEquipmentAsync("EQ001", "Furnace #1", "P01", "A01", "Furnace");

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("EQ001");
        repo.Verify(r => r.AddAsync(It.IsAny<Equipment>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateEquipment_duplicate_id_returns_conflict()
    {
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.ExistsAsync("EQ001", default)).ReturnsAsync(true);

        var result = await BuildService(repo).CreateEquipmentAsync("EQ001", "Furnace #1", "P01", "A01", "Furnace");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("already exists");
        repo.Verify(r => r.AddAsync(It.IsAny<Equipment>(), default), Times.Never);
    }

    // ── GetEquipmentAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetEquipment_existing_id_returns_equipment()
    {
        var eq = MakeEq();
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.GetByIdAsync("EQ001", default)).ReturnsAsync(eq);

        var result = await BuildService(repo).GetEquipmentAsync("EQ001");

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("EQ001");
    }

    [Fact]
    public async Task GetEquipment_unknown_id_returns_not_found()
    {
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.GetByIdAsync("GHOST", default)).ReturnsAsync((Equipment?)null);

        var result = await BuildService(repo).GetEquipmentAsync("GHOST");

        result.IsFailure.Should().BeTrue();
    }

    // ── GetEquipmentListAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetEquipmentList_returns_all_by_plant()
    {
        var list = new List<Equipment> { MakeEq("EQ001"), MakeEq("EQ002") };
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.GetAllByPlantAsync("P01", default))
            .ReturnsAsync((IReadOnlyList<Equipment>)list);

        var result = await BuildService(repo).GetEquipmentListAsync("P01");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    // ── DeactivateEquipmentAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DeactivateEquipment_sets_state_to_Invalid()
    {
        var eq = MakeEq();
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.GetByIdAsync("EQ001", default)).ReturnsAsync(eq);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Equipment>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).DeactivateEquipmentAsync("EQ001");

        result.IsSuccess.Should().BeTrue();
        eq.ValidState.Should().Be("Invalid");
    }

    [Fact]
    public async Task DeactivateEquipment_unknown_id_returns_not_found()
    {
        var repo = new Mock<IEquipmentRepository>();
        repo.Setup(r => r.GetByIdAsync("GHOST", default)).ReturnsAsync((Equipment?)null);

        var result = await BuildService(repo).DeactivateEquipmentAsync("GHOST");

        result.IsFailure.Should().BeTrue();
    }
}
