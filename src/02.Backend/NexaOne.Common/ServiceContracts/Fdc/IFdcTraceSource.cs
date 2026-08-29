using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// 한 소비 범위가 FDC의 영속 TRACE 원천에서 읽을 시간 구간과 재시작 커서를 정의한다.
/// <paramref name="ScopeId"/>는 FDC가 해석하지 않고 결과에 그대로 돌려주는 소비자 상관키다.
/// 모든 시각은 읽기 경계에서 UTC로 정규화되며, Kind가 없는 값은 기존 저장 계약에 따라 UTC로 해석한다.
/// </summary>
public sealed record FdcTraceReadScope(
    string ScopeId,
    string EquipmentId,
    string ParameterId,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    DateTime? AfterCollectedAt,
    string? AfterCollectId);

/// <summary>FDC가 소유한 영속 수집 원천에서 읽은 정밀도 보존 TRACE 표본이다.</summary>
public sealed record FdcTraceSample(
    string ScopeId,
    string CollectId,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    string Quality,
    DateTime CollectedAt);

/// <summary>
/// 요청한 TRACE 재개 지점보다 앞선 원천 표본이 이미 보존정리됐음을 명시한다. 소비자는 이 예외를
/// 빈 페이지로 취급하거나 커서를 자동 전진시키지 말고 운영 복구가 필요한 데이터 gap으로 처리해야 한다.
/// </summary>
public sealed class FdcTraceGapException : InvalidOperationException
{
    public FdcTraceGapException(
        string scopeId,
        DateTime requestedFrom,
        DateTime completenessBoundary)
        : base(
            $"FDC TRACE scope '{scopeId}' resumes at {requestedFrom:o}, before durable "
            + $"completeness boundary {completenessBoundary:o}.")
    {
        ScopeId = scopeId;
        RequestedFrom = requestedFrom;
        CompletenessBoundary = completenessBoundary;
    }

    public string ScopeId { get; }
    public DateTime RequestedFrom { get; }
    public DateTime CompletenessBoundary { get; }
}

/// <summary>
/// FDC 영속 TRACE 원천의 모듈 경계다. 소비 모듈은 FDC 테이블이나 저장 방언을 알지 않고,
/// 범위별 단조 커서 이후의 표본만 시간순으로 읽는다.
/// </summary>
public interface IFdcTraceSource : INexaModuleBridge
{
    Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
        IReadOnlyCollection<FdcTraceReadScope> scopes,
        int maxCount,
        CancellationToken ct = default);
}
