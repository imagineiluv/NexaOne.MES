using NexaOne.Common;

namespace NexaOne.POM.Domain;

/// <summary>설계 19.4.3 표준 상태 — 현행 LOTSTATE_* 상수 매핑.</summary>
public enum LotState
{
    Created,     // LOTSTATE_CREATED
    Queued,      // LOTSTATE_INPRODUCTION + READY/Idle (공정 대기)
    Processing,  // LOTPROCESSSTATE_RUN (TrackIn 후 생산 진행)
    Completed,   // LOTSTATE_FINISHED
    Consumed     // Mixing 투입으로 소비됨 (MATERIALLOTSTATE_CONSUMED의 Lot 적응)
}

/// <summary>TrackIn 중 여부 — 현행 LOTPROCESSSTATE_RUN/Idle.</summary>
public enum LotProcessState
{
    Idle,
    Run
}

/// <summary>
/// 설계 19.4.3 — 상태 전이는 문자열 직접 변경이 아니라 허용된 transition만 수행한다.
/// </summary>
public static class LotStateMachine
{
    private static readonly Dictionary<LotState, LotState[]> Allowed = new()
    {
        [LotState.Created] = [LotState.Queued, LotState.Consumed],
        [LotState.Queued] = [LotState.Processing, LotState.Consumed],
        [LotState.Processing] = [LotState.Queued, LotState.Completed],
        [LotState.Completed] = [],
        [LotState.Consumed] = []
    };

    public static bool CanTransition(LotState from, LotState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}

/// <summary>
/// 생산 Lot (설계서 19.4). 공정 경로는 현행 ProcessPathStack의 적응으로
/// 생성 시점에 확정되는 순서 목록(RouteSteps)이며, TrackOut 시 다음 공정으로 이동한다.
/// </summary>
public sealed class Lot : AuditableEntity<string>
{
    public const char RouteSeparator = '>';
    /// <summary>Every downstream process audit column is NVARCHAR(50).</summary>
    public const int MaxProcessIdLength = 50;
    /// <summary>ROUTE_STEPS NVARCHAR(500) — '>' 직렬화 결과가 컬럼 길이를 넘지 않게 제한.</summary>
    public const int MaxRouteStepsLength = 500;

    private readonly List<string> _routeSteps = [];

    private Lot(string lotId) : base(lotId) { }

    public string PlantId { get; private set; } = string.Empty;
    /// <summary>생산 지시(POM_PRODUCTION_ORDER) — Mixing 출력 Lot 등은 없을 수 있다.</summary>
    public string? WorkOrderId { get; private set; }
    public string ProductId { get; private set; } = string.Empty;
    public decimal Qty { get; private set; }
    public decimal DefectQty { get; private set; }
    public LotState State { get; private set; }
    public LotProcessState ProcessState { get; private set; }
    public IReadOnlyList<string> RouteSteps => _routeSteps;
    public int CurrentStepIndex { get; private set; }
    /// <summary>LOT 생성 후 실제 적용되는 라우팅 통제 수준. 기존 LOT은 Strict로 복원한다.</summary>
    public RoutingControlMode ControlMode { get; private set; } = RoutingControlMode.Strict;
    /// <summary>재작업 완료 후 되돌아갈 원 공정 인덱스. null이면 정상 라우팅 흐름이다.</summary>
    public int? ReturnStepIndex { get; private set; }
    public bool IsInRework => ReturnStepIndex.HasValue;
    /// <summary>현재(또는 마지막 처리) 공정 — 경로 끝을 지나면 마지막 공정을 유지한다.</summary>
    public string CurrentProcessId =>
        _routeSteps.Count == 0 ? string.Empty : _routeSteps[Math.Min(CurrentStepIndex, _routeSteps.Count - 1)];
    public bool IsLastStep => CurrentStepIndex >= _routeSteps.Count - 1;
    /// <summary>정상 흐름의 다음 공정 또는 재작업 후 복귀 공정 인덱스.</summary>
    public int? NextStepIndex => ReturnStepIndex ??
        (CurrentStepIndex + 1 < _routeSteps.Count ? CurrentStepIndex + 1 : null);
    public string? NextProcessId => NextStepIndex is int next && next >= 0 && next < _routeSteps.Count
        ? _routeSteps[next]
        : null;
    public string? ReturnProcessId => ReturnStepIndex is int step && step >= 0 && step < _routeSteps.Count
        ? _routeSteps[step]
        : null;
    public string? EquipmentId { get; private set; }
    public string? RecipeDefId { get; private set; }
    public int? RecipeDefVersion { get; private set; }
    public string? CarrierId { get; private set; }
    public bool IsHold { get; private set; }
    public string? TrackInUser { get; private set; }
    public DateTime? TrackInTime { get; private set; }
    public string? TrackOutUser { get; private set; }
    public DateTime? TrackOutTime { get; private set; }
    public int VersionNo { get; private set; } = 1;

    public static Result<Lot> Create(
        string lotId,
        string plantId,
        string? workOrderId,
        string productId,
        decimal qty,
        IReadOnlyList<string> routeSteps,
        string createdBy,
        RoutingControlMode routingControlMode = RoutingControlMode.Strict)
    {
        if (string.IsNullOrWhiteSpace(lotId))
            return Result.Failure<Lot>(Error.Validation(nameof(lotId), "Lot ID is required."));
        if (lotId.Trim().Length > PomStorageBoundary.IdentifierLength)
            return Result.Failure<Lot>(Error.Validation(
                nameof(lotId), $"Lot ID cannot exceed {PomStorageBoundary.IdentifierLength} characters."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<Lot>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<Lot>(Error.Validation(nameof(productId), "Product ID is required."));
        if (!TryNormalizeActor(createdBy, out var actor))
            return Result.Failure<Lot>(Error.Validation(
                nameof(createdBy), $"Created user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (qty <= 0)
            return Result.Failure<Lot>(Error.Validation(nameof(qty), "Lot quantity must be positive."));
        if (!ProductionQuantityBoundary.Fits(qty))
            return Result.Failure<Lot>(Error.Validation(
                nameof(qty), "Lot quantity must fit DECIMAL(18,4)."));

        var steps = (routeSteps ?? []).Select(s => (s ?? string.Empty).Trim())
            .Where(s => s.Length > 0).ToList();
        if (steps.Count == 0)
            return Result.Failure<Lot>(Error.Validation(nameof(routeSteps), "At least one route step is required."));
        if (steps.Any(s => s.Contains(RouteSeparator)))
            return Result.Failure<Lot>(Error.Validation(nameof(routeSteps), $"Route step cannot contain '{RouteSeparator}'."));
        if (steps.Any(s => s.Length > MaxProcessIdLength))
            return Result.Failure<Lot>(Error.Validation(
                nameof(routeSteps), $"Each route step cannot exceed {MaxProcessIdLength} characters."));
        if (string.Join(RouteSeparator, steps).Length > MaxRouteStepsLength)
            return Result.Failure<Lot>(Error.Validation(nameof(routeSteps), $"Route steps exceed {MaxRouteStepsLength} characters."));

        var lot = new Lot(lotId.Trim())
        {
            PlantId = plantId.Trim(),
            WorkOrderId = string.IsNullOrWhiteSpace(workOrderId) ? null : workOrderId.Trim(),
            ProductId = productId.Trim(),
            Qty = qty,
            State = LotState.Created,
            ProcessState = LotProcessState.Idle,
            ControlMode = routingControlMode
        };
        lot._routeSteps.AddRange(steps);
        lot.SetAudit(actor);
        // 설계 19.4.3: Created -> Queued. 별도 release 단계가 없는 웹 적응에서는 생성 즉시 공정 대기로 둔다.
        lot.State = LotState.Queued;
        return lot;
    }

    /// <summary>DB 행 복원용 — 저장된 상태 그대로 재구성한다(전이 검증 없음).</summary>
    public static Lot Restore(
        string lotId, string plantId, string? workOrderId, string productId,
        decimal qty, decimal defectQty, LotState state, LotProcessState processState,
        IReadOnlyList<string> routeSteps, int currentStepIndex,
        string? equipmentId, string? recipeDefId, int? recipeDefVersion, string? carrierId,
        bool isHold, string? trackInUser, DateTime? trackInTime,
        string? trackOutUser, DateTime? trackOutTime,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null,
        int versionNo = 1,
        RoutingControlMode routingControlMode = RoutingControlMode.Strict,
        int? returnStepIndex = null)
    {
        var lot = new Lot(lotId)
        {
            PlantId = plantId,
            WorkOrderId = workOrderId,
            ProductId = productId,
            Qty = qty,
            DefectQty = defectQty,
            State = state,
            ProcessState = processState,
            CurrentStepIndex = currentStepIndex,
            EquipmentId = equipmentId,
            RecipeDefId = recipeDefId,
            RecipeDefVersion = recipeDefVersion,
            CarrierId = carrierId,
            IsHold = isHold,
            TrackInUser = trackInUser,
            TrackInTime = trackInTime,
            TrackOutUser = trackOutUser,
            TrackOutTime = trackOutTime,
            VersionNo = versionNo,
            ControlMode = routingControlMode,
            ReturnStepIndex = returnStepIndex
        };
        lot._routeSteps.AddRange(routeSteps);
        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 그대로 복원(미복원 시 CreatedAt이 매 읽기마다 UtcNow로
        // 재생성되고 CreatedBy=""·UpdatedBy/At=null로 리셋됨). 인자 미제공 시 기본값(생성 시점 값)을 유지한다.
        lot.RestoreAudit(createdBy ?? lot.CreatedBy, createdAt ?? lot.CreatedAt, updatedBy, updatedAt);
        return lot;
    }

    /// <summary>설계 19.4.4 — Hold Lot 거부, Queued에서만 진입, 설비/Recipe/작업자/시각 설정.</summary>
    public Result TrackIn(string equipmentId, string? recipeDefId, int? recipeDefVersion, string user, DateTime serverTime)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (CurrentProcessId.Length > MaxProcessIdLength)
            return Result.Failure(Error.Validation(
                nameof(CurrentProcessId), $"Current process ID cannot exceed {MaxProcessIdLength} characters."));
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 TrackIn할 수 없습니다."));
        if (!LotStateMachine.CanTransition(State, LotState.Processing))
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 TrackIn할 수 없습니다."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure(Error.Validation(nameof(equipmentId), "Equipment ID is required."));

        State = LotState.Processing;
        ProcessState = LotProcessState.Run;
        EquipmentId = equipmentId.Trim();
        RecipeDefId = string.IsNullOrWhiteSpace(recipeDefId) ? null : recipeDefId.Trim();
        RecipeDefVersion = recipeDefVersion;
        TrackInUser = actor;
        TrackInTime = serverTime;
        TrackOutUser = null;
        TrackOutTime = null;
        UpdateAudit(actor);
        // ADR-002: TrackIn을 도메인 이벤트로 발행한다. 리포가 TrackIn(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new LotTrackedInDomainEvent(Id, EquipmentId, RecipeDefId, RecipeDefVersion, CurrentProcessId));
        return Result.Success();
    }

    /// <summary>
    /// 설계 19.4.5 — Processing에서만, TrackIn 설비와 일치해야 하며 수량은 Lot Qty 범위 내.
    /// 성공 시 설비/Recipe 점유를 반납하고(현행 TrackOutLotService) 다음 공정으로 이동하거나 완료한다.
    /// </summary>
    public Result TrackOut(string equipmentId, decimal qty, decimal defectQty, string? carrierId, string user, DateTime serverTime)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (CurrentProcessId.Length > MaxProcessIdLength)
            return Result.Failure(Error.Validation(
                nameof(CurrentProcessId), $"Current process ID cannot exceed {MaxProcessIdLength} characters."));
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 TrackOut할 수 없습니다."));
        if (State != LotState.Processing)
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 TrackOut할 수 없습니다."));
        if (!string.Equals(EquipmentId, equipmentId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict("TrackIn 설비와 TrackOut 설비가 일치해야 합니다."));
        if (qty <= 0)
            return Result.Failure(Error.Validation(nameof(qty), "생산수량은 0보다 커야 합니다."));
        if (defectQty < 0)
            return Result.Failure(Error.Validation(nameof(defectQty), "불량수량은 음수일 수 없습니다."));
        if (!ProductionQuantityBoundary.Fits(qty) || !ProductionQuantityBoundary.Fits(defectQty))
            return Result.Failure(Error.Validation(
                nameof(qty), "생산수량과 불량수량은 DECIMAL(18,4) 범위여야 합니다."));
        if (qty > Qty)
            return Result.Failure(Error.Validation(nameof(qty), "생산수량은 Lot 수량을 초과할 수 없습니다."));
        // Qty is the remaining/reported quantity after this operation, while DefectQty is the
        // cumulative defect quantity across operations. Validate the post-transition aggregate
        // before mutating it so the domain can never reach the DB constraint DEFECT_QTY > QTY.
        if (!ProductionQuantityBoundary.TryAdd(DefectQty, defectQty, out var cumulativeDefectQty))
            return Result.Failure(Error.Validation(
                nameof(defectQty), "Cumulative defect quantity must fit DECIMAL(18,4)."));
        if (cumulativeDefectQty > qty)
            return Result.Failure(Error.Validation(
                nameof(defectQty),
                "누적 불량수량은 현재 생산수량을 초과할 수 없습니다."));
        if (!string.IsNullOrWhiteSpace(carrierId) && carrierId.Trim().Length > 50)
            return Result.Failure(Error.Validation(
                nameof(carrierId), "Carrier ID cannot exceed 50 characters."));

        Qty = qty;
        DefectQty = cumulativeDefectQty;
        CarrierId = string.IsNullOrWhiteSpace(carrierId) ? CarrierId : carrierId.Trim();
        TrackOutUser = actor;
        TrackOutTime = serverTime;
        // 현행 TrackOutLotService: TrackOut 시 설비/Recipe 점유 반납
        EquipmentId = null;
        RecipeDefId = null;
        RecipeDefVersion = null;
        ProcessState = LotProcessState.Idle;

        // P0 재작업은 한 공정 방문으로 제한한다. 승인된 Rework가 원 공정을 이미 바인딩했으므로
        // 재작업 TrackOut과 동시에 자동 복귀해 별도의 두 번째 승인이나 재작업 무한 루프를 만들지 않는다.
        if (IsInRework)
        {
            var reworkStep = CurrentStepIndex;
            var returnStep = ReturnStepIndex!.Value;
            CurrentStepIndex = returnStep;
            ReturnStepIndex = null;
            State = LotState.Queued;
            RaiseDomainEvent(new LotRouteDeviationAppliedDomainEvent(
                Id, RouteDeviationType.Return, reworkStep, returnStep, ControlMode,
                "Automatic return after rework TrackOut", ExceptionId: null));
        }
        else if (IsLastStep)
        {
            State = LotState.Completed;
        }
        else
        {
            CurrentStepIndex++;
            State = LotState.Queued;
        }
        UpdateAudit(actor);
        // ADR-002: TrackOut을 도메인 이벤트로 발행한다. 리포가 TrackOut(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // 전이 후 상태(State/CurrentStepIndex/IsLastStep)를 담는다 — 구독자가 다음 공정 라우팅·완료를 판단하도록.
        RaiseDomainEvent(new LotTrackedOutDomainEvent(Id, Qty, DefectQty, State, CurrentStepIndex, IsLastStep));
        return Result.Success();
    }

    /// <summary>기존 Mixing 출력 Lot에 투입 합계를 가산 (설계 19.4.7 '기존 Mixing Lot 조회' 적응).</summary>
    public Result IncreaseMixingQty(decimal qty, string user)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 수량을 변경할 수 없습니다."));
        if (State != LotState.Queued)
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 Mixing 수량을 가산할 수 없습니다."));
        if (qty <= 0)
            return Result.Failure(Error.Validation(nameof(qty), "가산 수량은 0보다 커야 합니다."));

        if (!ProductionQuantityBoundary.TryAdd(Qty, qty, out var increasedQty))
            return Result.Failure(Error.Validation(
                nameof(qty), "Mixing quantity must fit DECIMAL(18,4)."));

        Qty = increasedQty;
        UpdateAudit(actor);
        return Result.Success();
    }

    /// <summary>Mixing 투입 소비 — Created/Queued에서만 가능 (설계 19.4.7).
    /// <para>의도된 '원자적 소비': 투입 Lot은 수량과 무관하게 전체가 Consumed로 전이된다(잔량 분할 없음).
    /// 투입량(inQty)은 LotMixingRelation/LotHistory에 genealogy(추적성/수율)로 기록될 뿐 재고 차감이 아니다.
    /// Lot 모델은 부분소비 상태/잔량 개념이 없는 원자 단위로 설계됐다(2026-06-14 결정: 현행 유지).
    /// 부분소비가 필요해지면 도메인 모델(상태·잔량) 확장이 선행돼야 하며 단순 수량 차감으로 바꾸지 말 것.</para></summary>
    public Result Consume(string user)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 소비할 수 없습니다."));
        if (!LotStateMachine.CanTransition(State, LotState.Consumed))
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 Mixing 투입으로 소비할 수 없습니다."));

        State = LotState.Consumed;
        ProcessState = LotProcessState.Idle;
        UpdateAudit(actor);
        // ADR-002: 소비를 도메인 이벤트로 발행한다. 리포가 소비(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new LotConsumedDomainEvent(Id, ProductId, Qty));
        return Result.Success();
    }

    public Result Hold(string user)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (State is LotState.Completed or LotState.Consumed)
            return Result.Failure(Error.Conflict("종결된 Lot은 Hold할 수 없습니다."));
        IsHold = true;
        UpdateAudit(actor);
        return Result.Success();
    }

    public Result ReleaseHold(string user)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        IsHold = false;
        UpdateAudit(actor);
        return Result.Success();
    }

    /// <summary>관리자가 LOT에 실제 적용할 순서 통제 수준을 변경한다. 변경 사유는 서비스 이력에 기록된다.</summary>
    public Result ChangeRoutingControlMode(RoutingControlMode controlMode, string reason, string user)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (State is LotState.Completed or LotState.Consumed)
            return Result.Failure(Error.Conflict("종결된 Lot의 라우팅 통제 모드는 변경할 수 없습니다."));
        if (ProcessState != LotProcessState.Idle)
            return Result.Failure(Error.Conflict("공정 실행 중에는 라우팅 통제 모드를 변경할 수 없습니다."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(nameof(reason), "라우팅 통제 모드 변경 사유가 필요합니다."));

        var previous = ControlMode;
        ControlMode = controlMode;
        UpdateAudit(actor);
        RaiseDomainEvent(new LotRoutingControlModeChangedDomainEvent(
            Id, previous, controlMode, reason.Trim(), actor));
        return Result.Success();
    }

    /// <summary>
    /// 정책 평가가 허용한 공정 이탈을 LOT에 반영한다. 이 메서드는 Hold·실행 상태·방향·복귀점 같은
    /// 절대 불변식만 소유하며 Strict/Flexible/NoControl 판정은 <see cref="RoutingPolicyEvaluator"/>가 담당한다.
    /// </summary>
    public Result ApplyRouteDeviation(
        RouteDeviationType deviationType,
        int targetStepIndex,
        string reason,
        string user,
        string? exceptionId = null)
    {
        if (!TryNormalizeActor(user, out var actor))
            return Result.Failure(Error.Validation(
                nameof(user), $"Execution user cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var validation = ValidateRouteDeviation(deviationType, targetStepIndex);
        if (validation.IsFailure) return validation;
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(nameof(reason), "라우팅 예외 사유가 필요합니다."));

        var from = CurrentStepIndex;
        if (deviationType == RouteDeviationType.Rework)
            ReturnStepIndex = CurrentStepIndex;
        else if (deviationType == RouteDeviationType.Return)
            ReturnStepIndex = null;

        if (deviationType is RouteDeviationType.Alternative or RouteDeviationType.SequenceChange)
        {
            // 선택한 남은 공정을 현재 위치로 이동하고 기존 현재 공정을 바로 다음에 남긴다.
            // 예: 10,20,30,40에서 current=20, target=30이면 10,30,20,40 순서가 된다.
            var selected = _routeSteps[targetStepIndex];
            _routeSteps.RemoveAt(targetStepIndex);
            _routeSteps.Insert(CurrentStepIndex, selected);
        }
        else
        {
            CurrentStepIndex = targetStepIndex;
        }
        State = LotState.Queued;
        ProcessState = LotProcessState.Idle;
        EquipmentId = null;
        RecipeDefId = null;
        RecipeDefVersion = null;
        TrackInUser = null;
        TrackInTime = null;
        TrackOutUser = null;
        TrackOutTime = null;
        UpdateAudit(actor);
        RaiseDomainEvent(new LotRouteDeviationAppliedDomainEvent(
            Id, deviationType, from, targetStepIndex, ControlMode,
            reason.Trim(), exceptionId));
        return Result.Success();
    }

    /// <summary>공정 이탈의 방향과 LOT 실행 상태를 검증하는 절대 불변식이다.</summary>
    public Result ValidateRouteDeviation(RouteDeviationType deviationType, int targetStepIndex)
    {
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot의 라우팅은 변경할 수 없습니다."));
        var isQueued = State == LotState.Queued && ProcessState == LotProcessState.Idle;
        var isRunningReworkSource = deviationType == RouteDeviationType.Rework &&
            State == LotState.Processing && ProcessState == LotProcessState.Run;
        if (!isQueued && !isRunningReworkSource)
            return Result.Failure(Error.Conflict(
                deviationType == RouteDeviationType.Rework
                    ? "Rework는 공정 대기 또는 실행 중인 Lot에만 적용할 수 있습니다."
                    : "공정 대기 상태의 Lot만 해당 라우팅 변경을 적용할 수 있습니다."));
        if (targetStepIndex < 0 || targetStepIndex >= _routeSteps.Count)
            return Result.Failure(Error.Validation(nameof(targetStepIndex), "대상 공정 인덱스가 라우팅 범위를 벗어났습니다."));

        // Return is an internal TrackOut result. Allowing callers to apply it directly would move
        // a LOT back to the saved step without actually executing and completing its rework step.
        if (deviationType == RouteDeviationType.Return)
            return Result.Failure(Error.Conflict(
                "Return is automatic after rework TrackOut and cannot be applied directly."));

        return deviationType switch
        {
            RouteDeviationType.Normal when targetStepIndex != CurrentStepIndex =>
                Result.Failure(Error.Validation(nameof(targetStepIndex), "정상 실행 대상은 현재 공정이어야 합니다.")),
            RouteDeviationType.Bypass or RouteDeviationType.Alternative or RouteDeviationType.SequenceChange
                when IsInRework => Result.Failure(Error.Conflict("재작업 중에는 복귀 외의 순방향 변경을 적용할 수 없습니다.")),
            RouteDeviationType.Bypass or RouteDeviationType.Alternative or RouteDeviationType.SequenceChange
                when targetStepIndex <= CurrentStepIndex => Result.Failure(Error.Validation(
                    nameof(targetStepIndex), "Bypass/Alternative/SequenceChange는 남은 순방향 공정만 선택할 수 있습니다.")),
            RouteDeviationType.Rework when IsInRework =>
                Result.Failure(Error.Conflict("이미 재작업 중인 Lot에 중첩 재작업을 적용할 수 없습니다.")),
            RouteDeviationType.Rework when targetStepIndex >= CurrentStepIndex => Result.Failure(Error.Validation(
                nameof(targetStepIndex), "Rework 대상은 현재 공정보다 앞선 공정이어야 합니다.")),
            RouteDeviationType.Return when !IsInRework =>
                Result.Failure(Error.Conflict("복귀할 재작업 원 공정이 없습니다.")),
            RouteDeviationType.Return when targetStepIndex != ReturnStepIndex => Result.Failure(Error.Conflict(
                "Return 대상은 재작업 시작 시 저장된 원 공정과 일치해야 합니다.")),
            _ => Result.Success()
        };
    }

    internal void AcceptPersistedVersion() => VersionNo++;

    private static bool TryNormalizeActor(string? value, out string actor)
    {
        actor = string.IsNullOrWhiteSpace(value) ? "SYSTEM" : value.Trim();
        return actor.Length <= PomStorageBoundary.ActorLength;
    }
}
