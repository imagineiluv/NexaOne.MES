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
    /// ABI 호환을 위해 남겨 둔 legacy 메서드다. IVT low-watermark 검증을 우회할 수 있으므로 항상
    /// 거부하며, 보존 정리는 IVT low-watermark를 검증한 전용 worker만 수행한다.
    /// </summary>
    [Obsolete("Use FdcCollectDataRetentionWorker.")]
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <summary>
/// 기존 <see cref="IFdcCollectDataRepository"/> consumer/implementation ABI를 깨지 않고 보존 정리의 운영 진단을
/// 제공하는 선택적 확장 seam이다. 운영 repository는 이 계약을 함께 구현한다.
/// </summary>
internal interface IFdcCollectDataRetentionRepository
{
    Task<FdcRetentionPurgeResult> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <summary>
/// FDC가 이미 불완전할 수 있다고 선언한 raw TRACE 시간 경계를 읽는다. 기존 collect repository ABI와
/// 분리해 legacy consumer의 메서드 표면을 바꾸지 않는다.
/// </summary>
public interface IFdcTraceRetentionStateRepository
{
    Task<FdcTraceRetentionState> GetTraceRetentionStateAsync(CancellationToken ct = default);
}

public sealed record FdcTraceRetentionState(DateTime CompletenessBoundary);

internal sealed record FdcRetentionPurgeResult(
    int DeletedRows,
    bool BatchLimitReached,
    DateTime? OldestRemainingCollectedAt,
    TimeSpan Elapsed);
