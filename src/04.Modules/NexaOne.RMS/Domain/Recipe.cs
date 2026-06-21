using NexaOne.Common;

namespace NexaOne.RMS.Domain;

public enum RecipeApprovalState { Draft, WaitApproval, Approved1, Approved, Released, Rejected }

public sealed class Recipe : AuditableEntity<string>
{
    private Recipe(string recipeId) : base(recipeId) { }

    public string RecipeName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string EquipmentClassId { get; private set; } = string.Empty;
    public int Version { get; private set; } = 1;
    public RecipeApprovalState ApprovalState { get; private set; }
    public string? FirstApproverId { get; private set; }
    public string? SecondApproverId { get; private set; }
    public DateTime? ReleasedAt { get; private set; }

    public static Result<Recipe> Create(
        string recipeId,
        string recipeName,
        string description,
        string equipmentClassId)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
            return Result.Failure<Recipe>(Error.Validation(nameof(recipeId), "Recipe ID is required."));
        if (string.IsNullOrWhiteSpace(recipeName))
            return Result.Failure<Recipe>(Error.Validation(nameof(recipeName), "Recipe name is required."));
        if (string.IsNullOrWhiteSpace(equipmentClassId))
            return Result.Failure<Recipe>(Error.Validation(nameof(equipmentClassId), "Equipment class ID is required."));

        var recipe = new Recipe(recipeId)
        {
            RecipeName = recipeName,
            Description = description,
            EquipmentClassId = equipmentClassId,
            Version = 1,
            ApprovalState = RecipeApprovalState.Draft
        };
        return recipe;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 항상 Draft/Version=1로 강제하므로 승인 진행/배포된 레시피가 Draft로 잘못 읽히는 것을 막는다.</summary>
    public static Recipe Restore(
        string recipeId, string recipeName, string description, string equipmentClassId,
        int version, RecipeApprovalState approvalState, string? firstApproverId,
        string? secondApproverId, DateTime? releasedAt,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var recipe = new Recipe(recipeId)
        {
            RecipeName = recipeName,
            Description = description,
            EquipmentClassId = equipmentClassId,
            Version = version,
            ApprovalState = approvalState,
            FirstApproverId = firstApproverId,
            SecondApproverId = secondApproverId,
            ReleasedAt = releasedAt
        };
        // 감사 메타데이터도 행값 그대로 복원한다 — 미복원 시 매 읽기마다 CreatedAt=UtcNow/CreatedBy="" 리셋(읽기경로 상태손실).
        recipe.RestoreAudit(createdBy ?? recipe.CreatedBy, createdAt ?? recipe.CreatedAt, updatedBy, updatedAt);
        return recipe;
    }

    public Result RequestApproval()
    {
        if (ApprovalState != RecipeApprovalState.Draft)
            return Result.Failure(Error.Conflict($"Recipe must be in Draft state to request approval. Current state: {ApprovalState}."));

        ApprovalState = RecipeApprovalState.WaitApproval;
        // ADR-002: 승인요청을 도메인 이벤트로 발행한다. 리포가 승인요청(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new RecipeApprovalRequestedDomainEvent(Id, ApprovalState.ToString()));
        return Result.Success();
    }

    public Result Approve1(string approverId)
    {
        if (ApprovalState != RecipeApprovalState.WaitApproval)
            return Result.Failure(Error.Conflict($"Recipe must be in WaitApproval state for first approval. Current state: {ApprovalState}."));
        if (string.IsNullOrWhiteSpace(approverId))
            return Result.Failure(Error.Validation(nameof(approverId), "Approver ID is required."));

        ApprovalState = RecipeApprovalState.Approved1;
        FirstApproverId = approverId;
        // ADR-002: 1차 승인을 도메인 이벤트로 발행한다. 리포가 1차 승인(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new RecipeFirstApprovedDomainEvent(Id, ApprovalState.ToString(), approverId));
        return Result.Success();
    }

    public Result Approve2(string approverId)
    {
        if (ApprovalState != RecipeApprovalState.Approved1)
            return Result.Failure(Error.Conflict($"Recipe must be in Approved1 state for second approval. Current state: {ApprovalState}."));
        if (string.IsNullOrWhiteSpace(approverId))
            return Result.Failure(Error.Validation(nameof(approverId), "Approver ID is required."));
        if (approverId == FirstApproverId)
            return Result.Failure(Error.Conflict("Second approver must be different from first approver."));

        ApprovalState = RecipeApprovalState.Approved;
        SecondApproverId = approverId;
        // ADR-002: 2차(최종) 승인을 도메인 이벤트로 발행한다. 리포가 2차 승인(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new RecipeApprovedDomainEvent(Id, ApprovalState.ToString(), approverId));
        return Result.Success();
    }

    public Result Release(string releaserId)
    {
        if (ApprovalState != RecipeApprovalState.Approved)
            return Result.Failure(Error.Conflict($"Recipe must be in Approved state to release. Current state: {ApprovalState}."));
        if (string.IsNullOrWhiteSpace(releaserId))
            return Result.Failure(Error.Validation(nameof(releaserId), "Releaser ID is required."));

        ApprovalState = RecipeApprovalState.Released;
        ReleasedAt = DateTime.UtcNow;
        // ADR-002: 배포를 도메인 이벤트로 발행한다. 리포가 배포(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new RecipeReleasedDomainEvent(Id, ApprovalState.ToString(), releaserId, ReleasedAt.Value));
        return Result.Success();
    }

    public Result Reject(string reason)
    {
        // 릴리스(승인 완료)된 레시피는 반려할 수 없다 — 승인 불변식 보호.
        if (ApprovalState == RecipeApprovalState.Released)
            return Result.Failure(Error.Conflict($"Released recipe cannot be rejected. Current state: {ApprovalState}."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(nameof(reason), "Rejection reason is required."));

        ApprovalState = RecipeApprovalState.Rejected;
        // ADR-002: 반려를 도메인 이벤트로 발행한다. 리포가 반려(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new RecipeRejectedDomainEvent(Id, ApprovalState.ToString(), reason));
        return Result.Success();
    }

    public Recipe CreateNewVersion(string newId) => new(newId)
    {
        RecipeName = RecipeName,
        Description = Description,
        EquipmentClassId = EquipmentClassId,
        Version = Version + 1,
        ApprovalState = RecipeApprovalState.Draft
    };

    // Parameterless overload kept for unit tests; production code should supply a new ID
    public Recipe CreateNewVersion() => CreateNewVersion(Id);
}
