namespace NexaOne.ServiceContracts.Sys;

/// <summary>승인 요청 등록에 필요한 최소 입력입니다. DocKind는 문서군 식별자(예: "Recipe", "FourMChange").</summary>
public sealed record ApprovalRequest(
    string DocKind,
    string DocId,
    string? Title = null);

/// <summary>승인/반려 결정 입력입니다. Approve=true면 Approved, false면 Rejected로 전이한다.</summary>
public sealed record ApprovalDecision(
    string ApprovalId,
    bool Approve,
    string? Comment = null);

/// <summary>문서의 현재 승인 상태 스냅샷입니다.</summary>
public sealed record ApprovalRecord(
    string ApprovalId,
    string DocKind,
    string DocId,
    string Status,           // Pending | Approved | Rejected | Cancelled
    string RequestedBy,
    DateTime RequestedAt,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? Comment);

/// <summary>승인 전이 1건의 append-only 이력입니다.</summary>
public sealed record ApprovalTransition(
    string ApprovalId,
    string DocKind,
    string DocId,
    string FromStatus,
    string ToStatus,
    string ChangedBy,
    DateTime ChangedAt,
    string? Reason);

/// <summary>
/// SYS가 소유하는 범용 승인 계약입니다. COM_APPROVAL은 문서별 현재 요청 1행을,
/// COM_APPROVAL_HISTORY는 전이 이력을 append-only로 보존한다. Submit/Decide/Cancel은 모두
/// 멱등 키와 요청 해시로 재시도·중복 제출을 방어한다. 소비 모듈은 COM_APPROVAL* 물리 스키마를 건드리지 않는다.
/// </summary>
public interface IApprovalProcess : INexaModuleBridge
{
    /// <summary>문서에 승인을 요청한다. 같은 (docKind, docId)의 Pending 요청이 있으면 그 ApprovalId를 돌려준다.</summary>
    Task<string> SubmitAsync(
        ApprovalRequest request,
        string requestedBy,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct = default);

    /// <summary>Pending 요청을 Approved/Rejected로 결정한다. 결정된 요청의 재결정은 거절한다.</summary>
    Task DecideAsync(
        ApprovalDecision decision,
        string decidedBy,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct = default);

    /// <summary>요청자가 본인의 Pending 요청을 취소한다.</summary>
    Task CancelAsync(
        string approvalId,
        string cancelledBy,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct = default);

    /// <summary>문서의 현재 승인 상태를 읽는다. 요청 이력이 없으면 null.</summary>
    Task<ApprovalRecord?> GetCurrentAsync(string docKind, string docId, CancellationToken ct = default);

    /// <summary>문서의 전이 이력을 시간순으로 읽는다.</summary>
    Task<IReadOnlyList<ApprovalTransition>> GetHistoryAsync(
        string docKind, string docId, CancellationToken ct = default);

    /// <summary>docKind 필터(선택)로 Pending 요청을 조회한다.</summary>
    Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(string? docKind = null, CancellationToken ct = default);
}
