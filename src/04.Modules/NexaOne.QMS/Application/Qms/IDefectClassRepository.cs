using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public interface IDefectClassRepository
{
    Task<DefectClass?> GetByIdAsync(string defectClassId, CancellationToken ct = default);
    Task<IReadOnlyList<DefectClass>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(DefectClass defectClass, CancellationToken ct = default);
    Task UpdateAsync(DefectClass defectClass, CancellationToken ct = default);
}
