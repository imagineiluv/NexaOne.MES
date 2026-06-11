using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public interface IDefectRepository
{
    Task<IReadOnlyList<Defect>> GetByLotAsync(string lotId, CancellationToken ct = default);
    Task<IReadOnlyList<Defect>> GetByEquipmentAsync(string equipmentId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<Defect?> GetByIdAsync(string defectId, CancellationToken ct = default);
    Task AddAsync(Defect defect, CancellationToken ct = default);
    Task UpdateAsync(Defect defect, CancellationToken ct = default);
}
