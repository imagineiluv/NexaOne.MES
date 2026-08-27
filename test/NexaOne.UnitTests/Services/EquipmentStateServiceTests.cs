using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;

namespace NexaOne.UnitTests.Services;

public sealed class EquipmentStateServiceTests
{
    [Fact]
    public async Task Bootstrap_is_insert_only_and_transition_uses_database_version_guard()
    {
        var matrices = new Mock<IEquipmentStateMatrixRepository>();
        matrices.Setup(x => x.FindAsync("P1", "IDLE", "RUN", It.IsAny<CancellationToken>()))
            .ReturnsAsync(EquipmentStateMatrix.Create("P1", "IDLE", "RUN", true));
        var states = new Mock<IEquipmentStateRepository>();
        states.Setup(x => x.GetAsync("EQ1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((EquipmentCurrentState?)null);
        states.Setup(x => x.TryInitializeAsync(It.IsAny<EquipmentCurrentState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        states.Setup(x => x.TryChangeStateWithHistoryAsync(
                It.IsAny<EquipmentCurrentState>(), It.IsAny<EquipmentStateHistory>(), 1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await new EquipmentStateService(matrices.Object, states.Object)
            .ChangeStateAsync("EQ1", "P1", "RUN", "operator");

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStateId.Should().Be("RUN");
        result.Value.StateVersion.Should().Be(2);
        states.Verify(x => x.TryChangeStateWithHistoryAsync(
            It.IsAny<EquipmentCurrentState>(), It.IsAny<EquipmentStateHistory>(), 1,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Database_guard_loser_returns_conflict_instead_of_reporting_success()
    {
        var current = EquipmentCurrentState.Restore("EQ1", "P1", "IDLE", DateTime.UtcNow, 7);
        var winner = EquipmentCurrentState.Restore("EQ1", "P1", "DOWN", DateTime.UtcNow, 8);
        var matrices = new Mock<IEquipmentStateMatrixRepository>();
        matrices.Setup(x => x.FindAsync("P1", "IDLE", "RUN", It.IsAny<CancellationToken>()))
            .ReturnsAsync(EquipmentStateMatrix.Create("P1", "IDLE", "RUN", true));
        var states = new Mock<IEquipmentStateRepository>();
        states.SetupSequence(x => x.GetAsync("EQ1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(current)
            .ReturnsAsync(winner);
        states.Setup(x => x.TryChangeStateWithHistoryAsync(
                It.IsAny<EquipmentCurrentState>(), It.IsAny<EquipmentStateHistory>(), 7,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await new EquipmentStateService(matrices.Object, states.Object)
            .ChangeStateAsync("EQ1", "P1", "RUN", "operator", expectedVersion: 7);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EST.EquipmentState.ConcurrentChange");
    }

    [Fact]
    public async Task Existing_state_cannot_be_changed_through_another_plant()
    {
        var current = EquipmentCurrentState.Restore("EQ1", "P1", "IDLE", DateTime.UtcNow, 1);
        var states = new Mock<IEquipmentStateRepository>();
        states.Setup(x => x.GetAsync("EQ1", It.IsAny<CancellationToken>())).ReturnsAsync(current);
        var matrices = new Mock<IEquipmentStateMatrixRepository>();

        var result = await new EquipmentStateService(matrices.Object, states.Object)
            .ChangeStateAsync("EQ1", "P2", "RUN", "operator");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EST.EquipmentState.PlantMismatch");
        matrices.Verify(x => x.FindAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
