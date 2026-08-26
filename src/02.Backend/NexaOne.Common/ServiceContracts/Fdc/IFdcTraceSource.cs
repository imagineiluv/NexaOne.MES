using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Fdc;

/// <summary>
/// 한 소비 범위가 FDC의 영속 TRACE 원천에서 읽을 시간 구간과 재시작 커서를 정의한다.
/// <paramref name="ScopeId"/>는 FDC가 해석하지 않고 결과에 그대로 돌려주는 소비자 상관키다.
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
/// FDC 영속 TRACE 원천의 모듈 경계다. 소비 모듈은 FDC 테이블이나 저장 방언을 알지 않고,
/// 범위별 단조 커서 이후의 표본만 시간순으로 읽는다.
/// </summary>
[NexaModuleBridge("Fdc", "fdcTraceSource")]
public interface IFdcTraceSource : INexaModuleBridge
{
    Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
        IReadOnlyCollection<FdcTraceReadScope> scopes,
        int maxCount,
        CancellationToken ct = default);
}
