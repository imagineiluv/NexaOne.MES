using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Application.Equipments;

public interface IAreaRepository
{
    Task<Area?> GetByIdAsync(string areaId, CancellationToken ct = default);
    Task<IReadOnlyList<Area>> GetByPlantAsync(string plantId, CancellationToken ct = default);
    Task AddAsync(Area area, CancellationToken ct = default);
    Task UpdateAsync(Area area, CancellationToken ct = default);
}
