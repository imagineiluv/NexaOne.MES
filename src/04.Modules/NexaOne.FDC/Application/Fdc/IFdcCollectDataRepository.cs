using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public interface IFdcCollectDataRepository
{
    Task<IReadOnlyList<FdcCollectData>> GetByParameterAsync(string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FdcCollectData>> GetLatestAsync(string parameterId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FdcCollectData>> GetTraceAsync(
        string equipmentId,
        string parameterId,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        DateTime? afterCollectedAt,
        string? afterCollectId,
        int limit,
        CancellationToken ct = default);
    Task AddAsync(FdcCollectData data, CancellationToken ct = default);
    Task AddBatchAsync(IEnumerable<FdcCollectData> data, CancellationToken ct = default);
    /// <summary>
    /// 수집시각(COLLECTED_AT)이 <paramref name="cutoff"/> 이전인 시계열 행을 짧은 bounded batch로
    /// 삭제하고 이번 호출의 삭제 건수를 반환한다. 한 호출의 상한을 넘긴 backlog는 다음 실행이 이어서 처리한다.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
