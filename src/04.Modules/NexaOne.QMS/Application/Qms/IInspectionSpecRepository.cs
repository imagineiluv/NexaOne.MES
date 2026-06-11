using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public interface IInspectionSpecRepository
{
    Task<InspectionSpec?> GetByIdAsync(string specId, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionSpec>> GetByProcessAsync(string processId, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionSpec>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(InspectionSpec spec, CancellationToken ct = default);
}
