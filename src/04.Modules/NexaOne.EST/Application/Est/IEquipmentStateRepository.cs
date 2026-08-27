using NexaOne.EST.Domain;

namespace NexaOne.EST.Application.Est;

public interface IEquipmentStateRepository
{
    Task<EquipmentCurrentState?> GetAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentCurrentState>> GetByPlantAsync(string plantId, CancellationToken ct = default);
    /// <summary>설비의 최초 상태를 insert-only로 생성한다. 이미 존재하면 기존 상태를 덮어쓰지 않고 false를 반환한다.</summary>
    Task<bool> TryInitializeAsync(EquipmentCurrentState state, CancellationToken ct = default);
    /// <summary>expectedVersion과 일치할 때만 현재 상태·이력·outbox를 한 트랜잭션으로 기록한다.</summary>
    Task<bool> TryChangeStateWithHistoryAsync(
        EquipmentCurrentState state,
        EquipmentStateHistory history,
        int expectedVersion,
        CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateHistory>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default);
}
