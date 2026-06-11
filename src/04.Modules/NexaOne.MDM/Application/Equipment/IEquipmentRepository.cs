using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Application.Equipments;

public interface IEquipmentRepository
{
    Task<Equipment?> GetByIdAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Equipment>> GetAllByPlantAsync(string plantId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string equipmentId, CancellationToken ct = default);
    Task AddAsync(Equipment equipment, CancellationToken ct = default);
    Task UpdateAsync(Equipment equipment, CancellationToken ct = default);
}
