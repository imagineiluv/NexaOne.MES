using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Qms;

/// <summary>LOT의 현재 공정 완료 여부를 결정하는 품질 게이트 상태다.</summary>
public enum ProductionQualityStatus
{
    /// <summary>현재 공정에 활성 검사 규격이 없어 품질 검사가 필요하지 않다.</summary>
    NotRequired,

    /// <summary>필요한 모든 검사 규격이 확정·합격됐다.</summary>
    Passed,

    /// <summary>
    /// 필요한 검사 결과가 없거나 아직 확정되지 않았거나, 규격별 최신 증거가 취소·정정·재검으로
    /// 무효화되어 새 유효 증거를 기다리고 있다. 무효화된 최신 증거보다 오래된 결과로 되돌아가지 않는다.
    /// </summary>
    Pending,

    /// <summary>확정된 검사 결과 중 하나 이상이 불합격이다.</summary>
    Failed
}

/// <summary>
/// 호스트가 QMS 검사 결과를 POM용으로 축약한 품질 게이트 판정이다.
/// POM은 이 계약만 사용하므로 QMS 영속 모델이나 도메인 형식에 직접 의존하지 않는다.
/// </summary>
public sealed record ProductionQualityGateResult(
    ProductionQualityStatus Status,
    int RequiredSpecCount,
    int PassedSpecCount,
    string? BlockingSpecId = null)
{
    /// <summary>현재 공정에 검사해야 할 활성 규격이 있는지 나타낸다.</summary>
    public bool Required => Status != ProductionQualityStatus.NotRequired;

    /// <summary>LOT의 현재 공정을 완료 상태로 전환해도 되는지 나타낸다.</summary>
    public bool AllowsCompletion => Status is ProductionQualityStatus.NotRequired or ProductionQualityStatus.Passed;

    /// <summary>활성 검사 규격이 없는 판정을 생성한다.</summary>
    public static ProductionQualityGateResult NotRequired() =>
        new(ProductionQualityStatus.NotRequired, 0, 0);

    /// <summary>모든 활성 규격을 통과한 판정을 생성한다.</summary>
    public static ProductionQualityGateResult Passed(int requiredSpecCount) =>
        new(ProductionQualityStatus.Passed, requiredSpecCount, requiredSpecCount);

    /// <summary>검사 결과 입력 또는 확정을 기다리는 판정을 생성한다.</summary>
    public static ProductionQualityGateResult Pending(
        int requiredSpecCount, int passedSpecCount, string? blockingSpecId = null) =>
        new(ProductionQualityStatus.Pending, requiredSpecCount, passedSpecCount, blockingSpecId);

    /// <summary>확정된 불합격 검사로 차단된 판정을 생성한다.</summary>
    public static ProductionQualityGateResult Failed(
        int requiredSpecCount, int passedSpecCount, string? blockingSpecId = null) =>
        new(ProductionQualityStatus.Failed, requiredSpecCount, passedSpecCount, blockingSpecId);
}

/// <summary>
/// POM과 QMS 사이의 공정 완료 품질 게이트 계약이다.
/// QMS가 활성 검사 규격과 증거를 해석하고, POM은 LOT의 최종 공정 전이와
/// 해당 LOT이 연결된 작업지시의 직접 마감을 허용할지만 묻는다.
/// 작업지시는 QMS 영속 모델을 알지 않고 이 축약 계약만 재사용한다.
/// 현재 판정 범위는 LOT과 공정이며 작업지시 식별자로 검사 증거를 추가 필터링하지 않는다.
/// </summary>
[NexaModuleBridge("Qms", "qmsProductionQualityGateway")]
public interface IProductionQualityGateway : INexaModuleBridge
{
    /// <summary>
    /// LOT과 공정에 연결된 활성 규격별 최신 공정검사 증거를 평가한다.
    /// 최신 결과가 취소됐거나 정정·재검 후속 실행으로 대체됐다면 과거 결과로 대체하지 않고
    /// 새 유효 결과가 기록될 때까지 <see cref="ProductionQualityStatus.Pending"/>을 반환한다.
    /// </summary>
    /// <param name="lotId">완료 전이를 요청한 LOT 식별자다.</param>
    /// <param name="processId">검사 규격을 선택할 현재 공정 식별자다.</param>
    /// <param name="workOrderId">
    /// 향후 작업지시 단위 QMS 연결을 위한 예약 인수다. 현재 구현은 이 값으로 증거를 필터링하지 않으며
    /// <paramref name="lotId"/>와 <paramref name="processId"/>만 판정 경계로 사용한다.
    /// </param>
    /// <param name="ct">조회 취소 토큰이다.</param>
    /// <returns>완료 허용 여부와 규격별 진행 상태를 요약한 판정이다.</returns>
    Task<ProductionQualityGateResult> EvaluateAsync(
        string lotId,
        string processId,
        string? workOrderId,
        CancellationToken ct = default);
}
