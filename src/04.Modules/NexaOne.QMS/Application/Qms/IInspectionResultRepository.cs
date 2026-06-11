using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public interface IInspectionResultRepository
{
    Task<IReadOnlyList<InspectionResult>> GetByLotAsync(string lotId, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionResult>> GetBySpecAsync(string specId, CancellationToken ct = default);
    Task AddAsync(InspectionResult result, CancellationToken ct = default);
}
