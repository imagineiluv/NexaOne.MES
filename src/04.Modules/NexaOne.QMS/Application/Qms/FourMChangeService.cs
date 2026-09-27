using System.Data.Common;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.QMS.Application.Qms;

/// <summary>4M 변경 본문은 QMS가, 승인 상태기는 SYS가 소유합니다.</summary>
public sealed class FourMChangeService
{
    private const string DocKind = "FourMChange";
    private readonly IFourMChangeRepository _changes;
    private readonly IApprovalProcess _approvals;

    public FourMChangeService(IFourMChangeRepository changes, IApprovalProcess approvals)
    {
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
    }

    public async Task<Result<FourMChangeDto>> SubmitAsync(
        SubmitFourMChangeDto request, string actorId, CancellationToken ct = default)
    {
        var error = ValidateSubmit(request, actorId);
        if (error is not null) return Result.Failure<FourMChangeDto>(error);

        var id = request.ChangeId.Trim();
        var actor = actorId.Trim();
        var key = request.IdempotencyKey.Trim();
        var changeType = NormalizeChangeType(request.ChangeType)!;
        var change = new FourMChange(
            id, Optional(request.ChangeNo), changeType, Optional(request.EquipmentId),
            Optional(request.ProductId), request.ChangeDate, request.Description.Trim(),
            "Pending", actor);
        var hash = CanonicalRequestHash.Compute(
            "FourMChange.Submit", change.ChangeId, change.ChangeNo, change.ChangeType,
            change.EquipmentId, change.ProductId, change.ChangeDate, change.Description, actor);
        var title = change.ChangeNo ?? $"{change.ChangeType} change {change.ChangeId}";

        try
        {
            await _approvals.SubmitWithDocumentAsync(
                new ApprovalRequest(DocKind, id, title), actor, key, hash,
                (transaction, token) => _changes.InsertAsync(change, transaction, token), ct);
        }
        catch (DbException)
        {
            if (await _changes.GetByIdAsync(id, ct) is not null)
                return Result.Failure<FourMChangeDto>(Error.Conflict(
                    "QMS.FourMChange.Duplicate", $"4M change '{id}' already exists."));
            throw;
        }
        catch (InvalidOperationException ex) when (IsApprovalConflict(ex))
        {
            return Result.Failure<FourMChangeDto>(Error.Conflict(
                "QMS.FourMChange.ApprovalConflict", ex.Message));
        }

        return await GetAsync(id, ct);
    }

    public async Task<Result<FourMChangeDto>> DecideAsync(
        string changeId, DecideFourMChangeDto decision, string actorId,
        CancellationToken ct = default)
    {
        var error = ValidateDecision(changeId, decision, actorId);
        if (error is not null) return Result.Failure<FourMChangeDto>(error);

        var id = changeId.Trim();
        var existing = await _changes.GetByIdAsync(id, ct);
        if (existing is null)
            return Result.Failure<FourMChangeDto>(Error.NotFoundOf(nameof(FourMChange), id));
        var approval = await _approvals.GetCurrentAsync(DocKind, id, ct);
        if (approval is null)
            return Result.Failure<FourMChangeDto>(Error.Conflict(
                "QMS.FourMChange.ApprovalMissing", $"4M change '{id}' has no shared approval request."));

        var actor = actorId.Trim();
        var key = decision.IdempotencyKey.Trim();
        var comment = Optional(decision.Comment);
        var hash = CanonicalRequestHash.Compute(
            "FourMChange.Decide", id, decision.Approve, comment, actor);
        var status = decision.Approve ? "Approved" : "Rejected";
        try
        {
            await _approvals.DecideWithDocumentAsync(
                new ApprovalDecision(approval.ApprovalId, decision.Approve, comment),
                actor, key, hash,
                async (transaction, token) =>
                {
                    if (!await _changes.TryDecideAsync(
                            id, status, actor, DateTime.UtcNow, transaction, token))
                        throw new InvalidOperationException(
                            $"APPROVAL_DOCUMENT_CONFLICT: 4M change '{id}' did not remain Pending.");
                }, ct);
        }
        catch (InvalidOperationException ex) when (IsApprovalConflict(ex)
            || ex.Message.StartsWith("APPROVAL_DOCUMENT_CONFLICT:", StringComparison.Ordinal))
        {
            return Result.Failure<FourMChangeDto>(Error.Conflict(
                "QMS.FourMChange.DecisionConflict", ex.Message));
        }

        return await GetAsync(id, ct);
    }

    public async Task<Result<FourMChangeDto>> GetAsync(string changeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(changeId))
            return Result.Failure<FourMChangeDto>(Error.Validation(nameof(changeId), "Change ID is required."));
        var id = changeId.Trim();
        var change = await _changes.GetByIdAsync(id, ct);
        if (change is null)
            return Result.Failure<FourMChangeDto>(Error.NotFoundOf(nameof(FourMChange), id));
        var approval = await _approvals.GetCurrentAsync(DocKind, id, ct);
        if (approval is null)
            return Result.Failure<FourMChangeDto>(Error.Conflict(
                "QMS.FourMChange.ApprovalMissing", $"4M change '{id}' has no shared approval request."));
        // 두 읽기 사이에 결정 트랜잭션이 커밋돼도 공통 승인 상태를 응답의 권위값으로 사용한다.
        return Result.Success(new FourMChangeDto(
            change.ChangeId, change.ChangeNo, change.ChangeType, change.EquipmentId,
            change.ProductId, change.ChangeDate, change.Description, approval.Status,
            approval.ApprovalId, approval.RequestedBy, approval.DecidedBy, approval.DecidedAt));
    }

    private static Error? ValidateSubmit(SubmitFourMChangeDto? request, string? actorId)
    {
        if (request is null)
            return Error.Validation(nameof(request), "4M change request is required.");
        if (string.IsNullOrWhiteSpace(request.ChangeId) || request.ChangeId.Trim().Length > 50)
            return Error.Validation(nameof(request.ChangeId), "Change ID must be 1-50 characters.");
        if (NormalizeChangeType(request.ChangeType) is null)
            return Error.Validation(nameof(request.ChangeType), "Change type must be Man, Machine, Material, or Method.");
        if (request.ChangeDate == default)
            return Error.Validation(nameof(request.ChangeDate), "Change date is required.");
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 1000)
            return Error.Validation(nameof(request.Description), "Description must be 1-1000 characters.");
        if (Optional(request.ChangeNo)?.Length > 50 || Optional(request.EquipmentId)?.Length > 50
            || Optional(request.ProductId)?.Length > 50)
            return Error.Validation(nameof(request), "Change number and reference IDs cannot exceed 50 characters.");
        return ValidateIdentity(actorId, request.IdempotencyKey);
    }

    private static Error? ValidateDecision(string? changeId, DecideFourMChangeDto? decision, string? actorId)
    {
        if (string.IsNullOrWhiteSpace(changeId) || changeId.Trim().Length > 50)
            return Error.Validation(nameof(changeId), "Change ID must be 1-50 characters.");
        if (decision is null)
            return Error.Validation(nameof(decision), "Decision is required.");
        if (!decision.Approve && string.IsNullOrWhiteSpace(decision.Comment))
            return Error.Validation(nameof(decision.Comment), "Rejection reason is required.");
        if (Optional(decision.Comment)?.Length > 500)
            return Error.Validation(nameof(decision.Comment), "Decision comment cannot exceed 500 characters.");
        return ValidateIdentity(actorId, decision.IdempotencyKey);
    }

    private static Error? ValidateIdentity(string? actorId, string? key)
    {
        if (string.IsNullOrWhiteSpace(actorId) || actorId.Trim().Length > 50)
            return Error.Validation(nameof(actorId), "Actor ID must be 1-50 characters.");
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 100)
            return Error.Validation(nameof(key), "Idempotency key must be 1-100 characters.");
        return null;
    }

    private static string? NormalizeChangeType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MAN" => "Man",
        "MACHINE" => "Machine",
        "MATERIAL" => "Material",
        "METHOD" => "Method",
        _ => null,
    };

    private static string? Optional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsApprovalConflict(InvalidOperationException ex)
        => ex.Message.StartsWith("APPROVAL_REQUEST_CONFLICT:", StringComparison.Ordinal);
}
