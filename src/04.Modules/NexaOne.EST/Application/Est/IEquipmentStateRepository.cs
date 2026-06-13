using NexaOne.EST.Domain;

namespace NexaOne.EST.Application.Est;

public interface IEquipmentStateRepository
{
    Task<EquipmentCurrentState?> GetAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentCurrentState>> GetByPlantAsync(string plantId, CancellationToken ct = default);
    Task UpsertAsync(EquipmentCurrentState state, CancellationToken ct = default);
    Task AddHistoryAsync(EquipmentStateHistory history, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateHistory>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default);
}
