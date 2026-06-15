using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcCollectDataRepository
{
    Task<IReadOnlyList<FdcCollectData>> GetByParameterAsync(string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FdcCollectData>> GetLatestAsync(string parameterId, int limit, CancellationToken ct = default);
    Task AddAsync(FdcCollectData data, CancellationToken ct = default);
    Task AddBatchAsync(IEnumerable<FdcCollectData> data, CancellationToken ct = default);
}
