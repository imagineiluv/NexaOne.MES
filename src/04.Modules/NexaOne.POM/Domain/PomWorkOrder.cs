using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>생산관리오더 아래에서 공정별 실제 실행을 표현하는 작업지시 상태.</summary>
public enum PomWorkOrderStatus
{
    Created,
    Released,
    Started,
    Completed,
    Cancelled
}

/// <summary>
/// 작업지시가 라우팅에 참여하는 범위를 구분한다.
/// Unbound는 수기/레거시 실행, Operation은 한 공정, SerialRoute는 제품 라우팅 전체 실행을 뜻한다.
/// </summary>
public enum PomWorkOrderRoutingScope
{
    Unbound,
    Operation,
    SerialRoute
}

/// <summary>
/// 현장 실행 작업지시 애그리거트다. 생산관리 지시인 <see cref="ProductionOrder"/>와 의도적으로 분리하며,
/// 공정 단위 또는 제품 라우팅 전체의 실행 범위와 계획/실적 수량, 상태 전이를 소유한다.
/// LOT/QMS 조회는 애그리거트 밖의 애플리케이션 완료 정책이 담당한다.
/// </summary>
public sealed class PomWorkOrder : AuditableEntity<string>
{
    private PomWorkOrder(string workOrderId) : base(workOrderId) { }

    // 부모 생산관리오더의 계획 데이터를 복제하지 않고, 공정 실행에 필요한 참조와 배정 정보만 보유한다.
    public string ProductionOrderId { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public string WorkOrderName { get; private set; } = string.Empty;
    public string? SalesOrderId { get; private set; }
    public string? AreaId { get; private set; }
    public string? EquipmentId { get; private set; }
    public string? WorkOrderType { get; private set; }
    public string ProductId { get; private set; } = string.Empty;
    public string? RoutingId { get; private set; }
    public PomWorkOrderRoutingScope RoutingScope { get; private set; }
    public int? RoutingStepNo { get; private set; }
    /// <summary>LOT 추적을 통해서만 시작·실적·완료할 수 있는 라우팅 작업지시인지 나타낸다.</summary>
    public bool IsRoutingBound => RoutingScope != PomWorkOrderRoutingScope.Unbound;
    /// <summary>하나의 작업지시가 제품 라우팅 전체를 순차 실행하는지 나타낸다.</summary>
    public bool IsSerialRouting => RoutingScope == PomWorkOrderRoutingScope.SerialRoute;
    public string? ProcessId { get; private set; }
    public string? WorkCenterId { get; private set; }

    // 수량은 작업지시 단위 절대 누계이며 VERSION_NO는 단말 간 낙관적 동시성 경계다.
    public DateTime? PlanStartDate { get; private set; }
    public DateTime? PlanEndDate { get; private set; }
    public decimal PlanQty { get; private set; }
    public decimal StartQty { get; private set; }
    public decimal CompleteQty { get; private set; }
    public decimal ScrapQty { get; private set; }
    public string? OwnerId { get; private set; }
    public PomWorkOrderStatus Status { get; private set; }
    public bool IsHold { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? Description { get; private set; }
    public int VersionNo { get; private set; } = 1;

    /// <summary>필수 식별자와 계획 수량을 검증해 Created 상태의 새 작업지시를 만든다.</summary>
    public static Result<PomWorkOrder> Create(
        string workOrderId,
        string productionOrderId,
        string plantId,
        string workOrderName,
        string productId,
        decimal planQty,
        DateTime? planStartDate,
        DateTime? planEndDate,
        string? processId,
        string? equipmentId,
        string? ownerId,
        string createdBy,
        string? routingId = null,
        int? routingStepNo = null,
        string? workCenterId = null,
        string? areaId = null,
        string? workOrderType = null,
        string? salesOrderId = null,
        string? description = null,
        PomWorkOrderRoutingScope? routingScope = null)
    {
        if (string.IsNullOrWhiteSpace(workOrderId))
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(workOrderId), "Work order ID is required."));
        if (string.IsNullOrWhiteSpace(productionOrderId))
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(productionOrderId), "Production order ID is required."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(productId), "Product ID is required."));
        if (planQty <= 0)
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(planQty), "Plan quantity must be positive."));
        if (!ProductionQuantityBoundary.Fits(planQty))
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(planQty), "Plan quantity must fit DECIMAL(18,4)."));
        if (planStartDate.HasValue && planEndDate.HasValue && planStartDate > planEndDate)
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(planStartDate), "Plan start must be on or before plan end."));
        var hasRoutingId = !string.IsNullOrWhiteSpace(routingId);
        var resolvedRoutingScope = routingScope ?? (hasRoutingId
            ? routingStepNo.HasValue
                ? PomWorkOrderRoutingScope.Operation
                : PomWorkOrderRoutingScope.SerialRoute
            : PomWorkOrderRoutingScope.Unbound);
        if (resolvedRoutingScope == PomWorkOrderRoutingScope.Unbound &&
            (hasRoutingId || routingStepNo.HasValue))
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(routingScope), "An unbound work order cannot specify routing identity."));
        if (resolvedRoutingScope == PomWorkOrderRoutingScope.Operation &&
            (!hasRoutingId || !routingStepNo.HasValue || string.IsNullOrWhiteSpace(processId)))
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(routingScope), "An operation work order requires routing ID, step number, and process ID."));
        if (resolvedRoutingScope == PomWorkOrderRoutingScope.SerialRoute &&
            (!hasRoutingId || routingStepNo.HasValue || !string.IsNullOrWhiteSpace(processId)))
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(routingScope), "A serial-route work order requires a routing ID and cannot bind one step or process."));
        if (routingStepNo is <= 0)
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(routingStepNo), "Routing step number must be positive."));
        var actor = AuditActor(createdBy);
        if (actor.IsFailure) return Result.Failure<PomWorkOrder>(actor.Error);

        var workOrder = new PomWorkOrder(workOrderId.Trim())
        {
            ProductionOrderId = productionOrderId.Trim(),
            PlantId = plantId.Trim(),
            WorkOrderName = string.IsNullOrWhiteSpace(workOrderName) ? workOrderId.Trim() : workOrderName.Trim(),
            SalesOrderId = Trimmed(salesOrderId),
            AreaId = Trimmed(areaId),
            EquipmentId = Trimmed(equipmentId),
            WorkOrderType = Trimmed(workOrderType),
            ProductId = productId.Trim(),
            RoutingId = Trimmed(routingId),
            RoutingScope = resolvedRoutingScope,
            RoutingStepNo = routingStepNo,
            ProcessId = Trimmed(processId),
            WorkCenterId = Trimmed(workCenterId),
            PlanStartDate = planStartDate,
            PlanEndDate = planEndDate,
            PlanQty = planQty,
            OwnerId = Trimmed(ownerId),
            Description = Trimmed(description),
            Status = PomWorkOrderStatus.Created
        };
        workOrder.SetAudit(actor.Value);
        return Result.Success(workOrder);
    }

    /// <summary>영속 행을 재검증 없이 애그리거트로 복원한다. 신규 생성 경로에서는 사용하지 않는다.</summary>
    public static PomWorkOrder Restore(
        string workOrderId, string productionOrderId, string plantId, string? workOrderName,
        string? salesOrderId, string? areaId, string? equipmentId, string? workOrderType,
        string productId, string? routingId, PomWorkOrderRoutingScope routingScope,
        int? routingStepNo, string? processId, string? workCenterId,
        DateTime? planStartDate, DateTime? planEndDate, decimal planQty, decimal startQty,
        decimal completeQty, decimal scrapQty, string? ownerId, PomWorkOrderStatus status,
        bool isHold, DateTime? startedAt, DateTime? completedAt, string? description,
        string? createdBy, DateTime? createdAt, string? updatedBy, DateTime? updatedAt,
        int versionNo)
    {
        var workOrder = new PomWorkOrder(workOrderId)
        {
            ProductionOrderId = productionOrderId,
            PlantId = plantId,
            WorkOrderName = workOrderName ?? workOrderId,
            SalesOrderId = salesOrderId,
            AreaId = areaId,
            EquipmentId = equipmentId,
            WorkOrderType = workOrderType,
            ProductId = productId,
            RoutingId = routingId,
            RoutingScope = routingScope,
            RoutingStepNo = routingStepNo,
            ProcessId = processId,
            WorkCenterId = workCenterId,
            PlanStartDate = planStartDate,
            PlanEndDate = planEndDate,
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
            VersionNo = versionNo
        };
        workOrder.RestoreAudit(createdBy ?? string.Empty, createdAt ?? DateTime.UtcNow, updatedBy, updatedAt);
        return workOrder;
    }

    /// <summary>Created 작업지시를 현장에서 실행할 수 있도록 Released 상태로 전환한다.</summary>
    public Result Release(string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkOrderStatus.Created)
            return Result.Failure(Error.Conflict("Work order can only be released from Created status."));
        Status = PomWorkOrderStatus.Released;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>Released 작업지시의 계획 수량을 시작 수량으로 확정하고 실행을 시작한다.</summary>
    public Result Start(DateTime startedAt, string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkOrderStatus.Released)
            return Result.Failure(Error.Conflict("Work order can only be started from Released status."));
        if (IsHold)
            return Result.Failure(Error.Conflict("A held work order cannot be started."));
        Status = PomWorkOrderStatus.Started;
        StartQty = PlanQty;
        StartedAt = startedAt;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>실적을 절대 누계로 설정해 같은 payload의 재시도를 자연스럽게 멱등 처리한다.</summary>
    public Result ReportProduction(decimal completeQty, decimal scrapQty, string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status != PomWorkOrderStatus.Started)
            return Result.Failure(Error.Conflict("Production can only be reported for a started work order."));
        if (IsHold)
            return Result.Failure(Error.Conflict("Production cannot be reported while the work order is held."));
        var valid = ValidateQuantities(completeQty, scrapQty);
        if (valid.IsFailure) return valid;
        CompleteQty = completeQty;
        ScrapQty = scrapQty;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>
    /// 최종 실적 수량을 검증·보고한 뒤 작업지시를 Completed 상태로 마감한다.
    /// 호출 전 LOT 종결·수량·QMS 판정은 PomWorkOrderService가 수행하므로 도메인은 외부 저장소를 참조하지 않는다.
    /// </summary>
    public Result Complete(decimal completeQty, decimal scrapQty, DateTime completedAt, string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        var valid = ValidateQuantities(completeQty, scrapQty);
        if (valid.IsFailure) return valid;
        if (completeQty + scrapQty <= 0)
            return Result.Failure(Error.Validation(nameof(completeQty), "A completed work order must have reported quantity."));
        var report = ReportProduction(completeQty, scrapQty, actor.Value);
        if (report.IsFailure) return report;
        Status = PomWorkOrderStatus.Completed;
        CompletedAt = completedAt;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>종료되지 않은 작업지시를 보류해 시작 및 실적 보고를 차단한다. 반복 보류는 성공으로 처리한다.</summary>
    public Result Hold(string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status is PomWorkOrderStatus.Completed or PomWorkOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("A terminal work order cannot be held."));
        if (IsHold) return Result.Success();
        IsHold = true;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>보류 상태를 해제하되 기존 작업지시 상태와 실적은 유지한다. 반복 해제는 성공으로 처리한다.</summary>
    public Result ReleaseHold(string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status is PomWorkOrderStatus.Completed or PomWorkOrderStatus.Cancelled)
            return Result.Failure(Error.Conflict("A terminal work order cannot be resumed."));
        if (!IsHold) return Result.Success();
        IsHold = false;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>아직 시작하지 않은 Created/Released 작업지시만 취소한다.</summary>
    public Result Cancel(string user)
    {
        var actor = AuditActor(user);
        if (actor.IsFailure) return Result.Failure(actor.Error);
        if (Status is not (PomWorkOrderStatus.Created or PomWorkOrderStatus.Released))
            return Result.Failure(Error.Conflict("Only a created or released work order can be cancelled."));
        Status = PomWorkOrderStatus.Cancelled;
        IsHold = false;
        UpdateAudit(actor.Value);
        return Result.Success();
    }

    /// <summary>양품·불량 누계가 음수가 아니며 시작 수량을 넘지 않는지 검증한다.</summary>
    private Result ValidateQuantities(decimal completeQty, decimal scrapQty)
    {
        if (completeQty < 0 || scrapQty < 0)
            return Result.Failure(Error.Validation(nameof(completeQty), "Complete and scrap quantities must be non-negative."));
        if (!ProductionQuantityBoundary.TryAdd(completeQty, scrapQty, out var totalQty))
            return Result.Failure(Error.Validation(
                nameof(completeQty), "Complete and scrap quantities must fit DECIMAL(18,4)."));
        var upper = StartQty > 0 ? StartQty : PlanQty;
        if (totalQty > upper)
            return Result.Failure(Error.Validation(nameof(completeQty), "Complete plus scrap quantity cannot exceed the started quantity."));
        return Result.Success();
    }

    /// <summary>감사 사용자 공백을 시스템 사용자로 정규화한다.</summary>
    private static string User(string? user) => string.IsNullOrWhiteSpace(user) ? "SYSTEM" : user.Trim();

    private static Result<string> AuditActor(string? user)
    {
        var actor = User(user);
        return actor.Length <= PomStorageBoundary.ActorLength
            ? Result.Success(actor)
            : Result.Failure<string>(Error.Validation(
                nameof(user), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
    }

    /// <summary>조건부 UPDATE가 성공한 뒤 메모리 애그리거트 버전을 DB와 맞춘다.</summary>
    internal void AcceptPersistedVersion() => VersionNo++;

    /// <summary>선택 문자열을 잘라내고 공백 값은 null로 통일한다.</summary>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
