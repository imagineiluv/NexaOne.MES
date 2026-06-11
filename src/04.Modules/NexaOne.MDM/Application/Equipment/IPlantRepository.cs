using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Application.Equipments;

public interface IPlantRepository
{
    Task<Plant?> GetByIdAsync(string plantId, CancellationToken ct = default);
    Task<IReadOnlyList<Plant>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Plant plant, CancellationToken ct = default);
    Task UpdateAsync(Plant plant, CancellationToken ct = default);
}
