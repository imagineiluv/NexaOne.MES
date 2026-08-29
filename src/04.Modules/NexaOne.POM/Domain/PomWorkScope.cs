using NexaOne.Common;
namespace NexaOne.POM.Domain;

/// <summary>생산 W/O에 종속되지 않는 작업 대상의 종류입니다.</summary>
public enum PomWorkScopeType
{
    Batch,
    Campaign,
    Carrier,
    Lot,
    Equipment,
    Other
}

/// <summary>생산 W/O 없이 설비 작업을 귀속할 수 있는 대상의 상태입니다.</summary>
public enum PomWorkScopeStatus
{
    Created,
    Released,
    Started,
    Completed,
    Cancelled
}

/// <summary>
/// 설비 작업의 실제 대상(Batch/Campaign/Carrier/Lot/Other)을 나타내는 독립 애그리거트입니다.
/// 기존 <see cref="PomWorkOrder"/>는 생산관리오더 하위의 레거시 생산 실행 계약으로 유지하고,
/// 이 애그리거트는 생산 LOT가 없는 Carrier 세척과 그룹 단위 Batch/Campaign 실행을 소유합니다.
/// </summary>
public sealed class PomWorkScope : AuditableEntity<string>
{
    private PomWorkScope(string id) : base(id) { }

    public string PlantId { get; private set; } = string.Empty;
    public PomWorkScopeType ScopeType { get; private set; }
    public string TargetId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? ParentScopeId { get; private set; }
    /// <summary>기존 생산 W/O와 연결할 때만 사용하는 선택적 상관관계 키입니다.</summary>
    public string? WorkOrderId { get; private set; }
    /// <summary>상위 작업 범위에서 실제 캐리어를 직접 귀속할 때 사용하는 선택 키입니다.</summary>
    public string? CarrierId { get; private set; }
    public string? EquipmentId { get; private set; }
    public string? ProductId { get; private set; }
    public string? ProcessId { get; private set; }
    public string? RecipeId { get; private set; }
    public int? RecipeVersion { get; private set; }
    public decimal? PlanQty { get; private set; }
    public decimal StartQty { get; private set; }
    public decimal CompleteQty { get; private set; }
    public decimal ScrapQty { get; private set; }
    public string? OwnerId { get; private set; }
    public PomWorkScopeStatus Status { get; private set; }
    public bool IsHold { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? Description { get; private set; }
    public int VersionNo { get; private set; } = 1;
    /// <summary>생성 명령 재전송을 판별하기 위한 선택적 멱등 키와 요청 해시입니다.</summary>
    public string? CreateIdempotencyKey { get; private set; }
    public string? CreateRequestHash { get; private set; }

    /// <summary>업무 대상의 식별자와 선택 계획 수량을 검증해 새 작업 대상을 만듭니다.</summary>
    public static Result<PomWorkScope> Create(
        string workScopeId,
        string plantId,
        PomWorkScopeType scopeType,
        string targetId,
        string? name,
        string? parentScopeId,
        string? equipmentId,
        string? productId,
        string? processId,
        string? recipeId,
        int? recipeVersion,
        decimal? planQty,
        string? ownerId,
        string? description,
        string createdBy,
        string? workOrderId = null,
        string? carrierId = null)
    {
        if (!PomStorageBoundary.FitsRequired(workScopeId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(workScopeId), $"Work scope ID is required and cannot exceed {PomStorageBoundary.IdentifierLength} characters."));
        if (!PomStorageBoundary.FitsRequired(plantId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(plantId), $"Plant ID is required and cannot exceed {PomStorageBoundary.IdentifierLength} characters."));
        if (!Enum.IsDefined(scopeType))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(scopeType), "Scope type is invalid."));
        if (string.IsNullOrWhiteSpace(targetId) || targetId.Trim().Length > 100)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(targetId), "Target ID is required and cannot exceed 100 characters."));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            return Result.Failure<PomWorkScope>(Error.Validation(
                nameof(name), "Name is required and cannot exceed 200 characters."));
        if (!PomStorageBoundary.FitsOptional(parentScopeId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(parentScopeId), "Parent scope ID is too long."));
        if (!PomStorageBoundary.FitsOptional(workOrderId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(workOrderId), "Work order ID is too long."));
        if (!PomStorageBoundary.FitsOptional(carrierId, 100))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(carrierId), "Carrier ID is too long."));
        if (!PomStorageBoundary.FitsOptional(equipmentId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(equipmentId), "Equipment ID is too long."));
        if (!PomStorageBoundary.FitsOptional(productId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(productId), "Product ID is too long."));
        if (!PomStorageBoundary.FitsOptional(processId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(processId), "Process ID is too long."));
        if (!PomStorageBoundary.FitsOptional(recipeId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(recipeId), "Recipe ID is too long."));
        if (recipeVersion is <= 0)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(recipeVersion), "Recipe version must be positive."));
        if (planQty is <= 0 || (planQty.HasValue && !ProductionQuantityBoundary.Fits(planQty.Value)))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(planQty), "Plan quantity must be positive and fit DECIMAL(18,4)."));
        if (!PomStorageBoundary.FitsOptional(ownerId, PomStorageBoundary.ActorLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(ownerId), "Owner ID is too long."));
        if (!PomStorageBoundary.FitsOptional(description, PomStorageBoundary.ReasonLength))
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(description), "Description is too long."));

        var actor = string.IsNullOrWhiteSpace(createdBy) ? "SYSTEM" : createdBy.Trim();
        if (actor.Length > PomStorageBoundary.ActorLength)
            return Result.Failure<PomWorkScope>(Error.Validation(nameof(createdBy), "Created by is too long."));

        // A carrier represents exactly one physical container. Giving it a unit count of one
        // makes the ordinary Report/Complete contract usable without inventing a production LOT.
        var normalizedTargetId = targetId.Trim();
        var normalizedCarrierId = Trimmed(carrierId);
        if (scopeType == PomWorkScopeType.Carrier)
        {
            if (normalizedCarrierId is not null
                && !string.Equals(normalizedCarrierId, normalizedTargetId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<PomWorkScope>(Error.Validation(
                    nameof(carrierId), "Carrier scope CarrierId must match TargetId."));
            normalizedCarrierId = normalizedTargetId;
        }

        var normalizedEquipmentId = Trimmed(equipmentId);
        if (scopeType == PomWorkScopeType.Equipment)
        {
            if (normalizedEquipmentId is not null
                && !string.Equals(normalizedEquipmentId, normalizedTargetId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<PomWorkScope>(Error.Validation(
                    nameof(equipmentId), "Equipment scope EquipmentId must match TargetId."));
            normalizedEquipmentId = normalizedTargetId;
        }

        var resolvedPlanQty = scopeType == PomWorkScopeType.Carrier ? planQty ?? 1m : planQty;
        var scope = new PomWorkScope(workScopeId.Trim())
        {
            PlantId = plantId.Trim(),
            ScopeType = scopeType,
            TargetId = normalizedTargetId,
            Name = name.Trim(),
            ParentScopeId = Trimmed(parentScopeId),
            WorkOrderId = Trimmed(workOrderId),
            CarrierId = normalizedCarrierId,
            EquipmentId = normalizedEquipmentId,
            ProductId = Trimmed(productId),
            ProcessId = Trimmed(processId),
            RecipeId = Trimmed(recipeId),
            RecipeVersion = recipeVersion,
            PlanQty = resolvedPlanQty,
            OwnerId = Trimmed(ownerId),
            Description = Trimmed(description),
            Status = PomWorkScopeStatus.Created
        };
        scope.SetAudit(actor);
        return Result.Success(scope);
    }

    /// <summary>영속 행을 검증 없이 복원합니다. 신규 생성은 Create 경로만 사용합니다.</summary>
    public static PomWorkScope Restore(
        string workScopeId,
        string plantId,
        PomWorkScopeType scopeType,
        string targetId,
        string name,
        string? parentScopeId,
        string? workOrderId,
        string? carrierId,
        string? equipmentId,
        string? productId,
        string? processId,
        string? recipeId,
        int? recipeVersion,
        decimal? planQty,
        decimal startQty,
        decimal completeQty,
        decimal scrapQty,
        string? ownerId,
        PomWorkScopeStatus status,
        bool isHold,
        DateTime? startedAt,
        DateTime? completedAt,
        string? description,
        int versionNo,
        string createdBy,
        DateTime createdAt,
        string? updatedBy,
        DateTime? updatedAt,
        string? createIdempotencyKey = null,
        string? createRequestHash = null)
    {
        var scope = new PomWorkScope(workScopeId)
        {
            PlantId = plantId,
            ScopeType = scopeType,
            TargetId = targetId,
            Name = name,
            ParentScopeId = parentScopeId,
            WorkOrderId = workOrderId,
            CarrierId = carrierId,
            EquipmentId = equipmentId,
            ProductId = productId,
            ProcessId = processId,
            RecipeId = recipeId,
            RecipeVersion = recipeVersion,
            PlanQty = planQty,
            StartQty = startQty,
            CompleteQty = completeQty,
            ScrapQty = scrapQty,
            OwnerId = ownerId,
            Status = status,
            IsHold = isHold,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Description = description,
            VersionNo = versionNo,
            CreateIdempotencyKey = createIdempotencyKey,
            CreateRequestHash = createRequestHash
        };
        scope.RestoreAudit(createdBy, createdAt, updatedBy, updatedAt);
        return scope;
    }

    public Result Release(string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkScopeStatus.Created)
            return Result.Failure(Error.Conflict("Work scope can only be released from Created status."));
        Status = PomWorkScopeStatus.Released;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    public Result Start(DateTime startedAt, string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkScopeStatus.Released)
            return Result.Failure(Error.Conflict("Work scope can only be started from Released status."));
        if (IsHold)
            return Result.Failure(Error.Conflict("A held work scope cannot be started."));
        Status = PomWorkScopeStatus.Started;
        StartQty = PlanQty ?? 0m;
        StartedAt = startedAt;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    public Result Report(decimal goodQty, decimal defectQty, string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkScopeStatus.Started)
            return Result.Failure(Error.Conflict("Work scope can only report production after it starts."));
        if (IsHold)
            return Result.Failure(Error.Conflict("Production cannot be reported while the work scope is held."));
        var valid = ValidateQuantities(goodQty, defectQty);
        if (valid.IsFailure) return valid;
        CompleteQty = goodQty;
        ScrapQty = defectQty;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    public Result Hold(string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status is PomWorkScopeStatus.Completed or PomWorkScopeStatus.Cancelled)
            return Result.Failure(Error.Conflict("A terminal work scope cannot be held."));
        if (IsHold) return Result.Success();
        IsHold = true;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    public Result ReleaseHold(string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status is PomWorkScopeStatus.Completed or PomWorkScopeStatus.Cancelled)
            return Result.Failure(Error.Conflict("A terminal work scope cannot resume."));
        if (!IsHold) return Result.Success();
        IsHold = false;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    public Result Complete(decimal goodQty, decimal defectQty, DateTime completedAt, string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkScopeStatus.Started)
            return Result.Failure(Error.Conflict("Work scope can only be completed after it starts."));
        if (IsHold)
            return Result.Failure(Error.Conflict("A held work scope cannot be completed."));
        var valid = ValidateQuantities(goodQty, defectQty);
        if (valid.IsFailure) return valid;
        if (goodQty + defectQty <= 0)
            return Result.Failure(Error.Validation(nameof(goodQty), "A completed work scope must have reported quantity."));
        CompleteQty = goodQty;
        ScrapQty = defectQty;
        Status = PomWorkScopeStatus.Completed;
        CompletedAt = completedAt;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    public Result Cancel(string user)
    {
        var actor = Actor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status is PomWorkScopeStatus.Completed or PomWorkScopeStatus.Cancelled)
            return Result.Failure(Error.Conflict("A completed or cancelled work scope cannot be cancelled."));
        Status = PomWorkScopeStatus.Cancelled;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    private Result ValidateQuantities(decimal goodQty, decimal defectQty)
    {
        if (goodQty < 0 || defectQty < 0)
            return Result.Failure(Error.Validation("Reported quantities cannot be negative."));
        if (!ProductionQuantityBoundary.Fits(goodQty) || !ProductionQuantityBoundary.Fits(defectQty))
            return Result.Failure(Error.Validation("Reported quantities must fit DECIMAL(18,4)."));
        if (!ProductionQuantityBoundary.TryAdd(goodQty, defectQty, out var total))
            return Result.Failure(Error.Validation("Reported quantities exceed DECIMAL(18,4)."));
        var upperBound = StartQty > 0m ? StartQty : PlanQty;
        if (upperBound.HasValue && total > upperBound.Value)
            return Result.Failure(Error.Validation("Reported quantities cannot exceed the planned quantity."));
        return Result.Success();
    }

    private static Result<string> Actor(string? user)
    {
        var actor = string.IsNullOrWhiteSpace(user) ? "SYSTEM" : user.Trim();
        return actor.Length <= PomStorageBoundary.ActorLength
            ? Result.Success(actor)
            : Result.Failure<string>(Error.Validation(nameof(user), "User cannot exceed 50 characters."));
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>낙관적 버전 조건부 UPDATE가 성공한 뒤 메모리 버전을 DB와 맞춥니다.</summary>
    internal void AcceptPersistedVersion() => VersionNo++;

    /// <summary>애플리케이션 서비스가 생성 명령의 재전송 판별 값을 확정합니다.</summary>
    internal void SetCreateIdentity(string idempotencyKey, string requestHash)
    {
        CreateIdempotencyKey = idempotencyKey;
        CreateRequestHash = requestHash;
    }
}
