using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>SYS 소유 범용 승인 프로세스를 형제 컨텍스트로 전달하는 부모 proxy입니다.</summary>
public sealed class ApprovalProcessProxy : IApprovalProcess
{
    private readonly ModuleBeanResolver _resolver;

    public ApprovalProcessProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<string> SubmitAsync(
        ApprovalRequest request, string requestedBy, string idempotencyKey, string requestHash,
        CancellationToken ct = default)
        => Resolve().SubmitAsync(request, requestedBy, idempotencyKey, requestHash, ct);

    public Task DecideAsync(
        ApprovalDecision decision, string decidedBy, string idempotencyKey, string requestHash,
        CancellationToken ct = default)
        => Resolve().DecideAsync(decision, decidedBy, idempotencyKey, requestHash, ct);

    public Task CancelAsync(
        string approvalId, string cancelledBy, string idempotencyKey, string requestHash,
        CancellationToken ct = default)
        => Resolve().CancelAsync(approvalId, cancelledBy, idempotencyKey, requestHash, ct);

    public Task<ApprovalRecord?> GetCurrentAsync(
        string docKind, string docId, CancellationToken ct = default)
        => Resolve().GetCurrentAsync(docKind, docId, ct);

    public Task<IReadOnlyList<ApprovalTransition>> GetHistoryAsync(
        string docKind, string docId, CancellationToken ct = default)
        => Resolve().GetHistoryAsync(docKind, docId, ct);

    public Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(
        string? docKind = null, CancellationToken ct = default)
        => Resolve().ListPendingAsync(docKind, ct);

    private IApprovalProcess Resolve() =>
        _resolver.Resolve<IApprovalProcess>("Sys", "approvalProcess");
}
