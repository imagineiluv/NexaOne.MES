using NexaOne.EST.Domain;

namespace NexaOne.EST.Application.Est;

public interface IEquipmentStateMatrixRepository
{
    Task<IReadOnlyList<EquipmentStateMatrix>> GetByPlantAsync(string plantId, CancellationToken ct = default);
    Task<EquipmentStateMatrix?> FindAsync(string plantId, string fromState, string toState, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateMatrix>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default);
    Task AddAsync(EquipmentStateMatrix matrix, CancellationToken ct = default);
    Task UpdateAsync(EquipmentStateMatrix matrix, CancellationToken ct = default);
}
