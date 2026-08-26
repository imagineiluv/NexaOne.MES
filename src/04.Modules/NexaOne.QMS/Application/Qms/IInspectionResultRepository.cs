using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

public interface IInspectionResultRepository
{
    Task<IReadOnlyList<InspectionResult>> GetByLotAsync(string lotId, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionResult>> GetBySpecAsync(string specId, CancellationToken ct = default);
    Task AddAsync(InspectionResult result, CancellationToken ct = default);

    /// <summary>서버 생성 검사 ID로 v2 집계와 append-only 이력을 조회합니다.</summary>
    Task<InspectionExecution?> GetExecutionAsync(string inspectionId, CancellationToken ct = default);

    /// <summary>전역 멱등키로 이미 확정된 실행을 조회해 동일 요청을 재생합니다.</summary>
    Task<InspectionExecution?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);

    /// <summary>선택한 계획 개정을 서버 원본에서 읽어 검사 시점 스냅샷을 만듭니다.</summary>
    Task<SamplingPlanRevision?> GetSamplingPlanRevisionAsync(
        string planRevisionId, CancellationToken ct = default);

    /// <summary>헤더·모든 항목·확정/연결 이벤트를 하나의 트랜잭션으로 저장합니다.</summary>
    Task AddExecutionAsync(
        InspectionExecution execution,
        InspectionExecutionHistory confirmation,
        InspectionExecutionHistory? parentRelation,
        CancellationToken ct = default);

    Task<InspectionExecutionHistory?> GetHistoryByIdempotencyKeyAsync(
        string inspectionId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Returns the single append-only cancellation event for an execution, if present.</summary>
    Task<InspectionExecutionHistory?> GetCancellationHistoryAsync(
        string inspectionId, CancellationToken ct = default);

    /// <summary>
    /// Computes the lot status from the latest non-superseded v2 execution while preserving
    /// the legacy all-row behavior when the lot has no v2 execution.
    /// </summary>
    Task<EffectiveLotInspectionStatus> GetEffectiveLotStatusAsync(
        string lotId, CancellationToken ct = default);

    /// <summary>기존 확정 행을 수정하지 않고 취소 같은 후속 사건만 추가합니다.</summary>
    Task AppendHistoryAsync(InspectionExecutionHistory history, CancellationToken ct = default);
}

/// <summary>
/// Effective quality state for a lot. <see cref="HasResults"/> is false when v2 evidence exists
/// but every execution is cancelled or superseded; callers must render that state as Pending with
/// zero result/failure counts instead of falling back to stale legacy evidence or showing Fail.
/// </summary>
public sealed record EffectiveLotInspectionStatus(
    bool HasResults,
    bool AllPassed,
    int ResultCount,
    int FailedCount,
    DateTime? LastInspectedAt);
