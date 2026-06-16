using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcCollectDataRepository
{
    Task<IReadOnlyList<FdcCollectData>> GetByParameterAsync(string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FdcCollectData>> GetLatestAsync(string parameterId, int limit, CancellationToken ct = default);
    Task AddAsync(FdcCollectData data, CancellationToken ct = default);
    Task AddBatchAsync(IEnumerable<FdcCollectData> data, CancellationToken ct = default);
    /// <summary>수집시각(COLLECTED_AT)이 <paramref name="cutoff"/> 이전인 시계열 행을 삭제하고 삭제 건수를 반환한다(보존정리용).</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
