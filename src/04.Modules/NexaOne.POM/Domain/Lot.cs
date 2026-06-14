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
    /// <summary>현재(또는 마지막 처리) 공정 — 경로 끝을 지나면 마지막 공정을 유지한다.</summary>
    public string CurrentProcessId =>
        _routeSteps.Count == 0 ? string.Empty : _routeSteps[Math.Min(CurrentStepIndex, _routeSteps.Count - 1)];
    public bool IsLastStep => CurrentStepIndex >= _routeSteps.Count - 1;
    public string? EquipmentId { get; private set; }
    public string? RecipeDefId { get; private set; }
    public int? RecipeDefVersion { get; private set; }
    public string? CarrierId { get; private set; }
    public bool IsHold { get; private set; }
    public string? TrackInUser { get; private set; }
    public DateTime? TrackInTime { get; private set; }
    public string? TrackOutUser { get; private set; }
    public DateTime? TrackOutTime { get; private set; }

    public static Result<Lot> Create(
        string lotId,
        string plantId,
        string? workOrderId,
        string productId,
        decimal qty,
        IReadOnlyList<string> routeSteps,
        string createdBy)
    {
        if (string.IsNullOrWhiteSpace(lotId))
            return Result.Failure<Lot>(Error.Validation(nameof(lotId), "Lot ID is required."));
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<Lot>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (string.IsNullOrWhiteSpace(productId))
            return Result.Failure<Lot>(Error.Validation(nameof(productId), "Product ID is required."));
        if (qty <= 0)
            return Result.Failure<Lot>(Error.Validation(nameof(qty), "Lot quantity must be positive."));

        var steps = (routeSteps ?? []).Select(s => (s ?? string.Empty).Trim())
            .Where(s => s.Length > 0).ToList();
        if (steps.Count == 0)
            return Result.Failure<Lot>(Error.Validation(nameof(routeSteps), "At least one route step is required."));
        if (steps.Any(s => s.Contains(RouteSeparator)))
            return Result.Failure<Lot>(Error.Validation(nameof(routeSteps), $"Route step cannot contain '{RouteSeparator}'."));
        if (string.Join(RouteSeparator, steps).Length > MaxRouteStepsLength)
            return Result.Failure<Lot>(Error.Validation(nameof(routeSteps), $"Route steps exceed {MaxRouteStepsLength} characters."));

        var lot = new Lot(lotId.Trim())
        {
            PlantId = plantId.Trim(),
            WorkOrderId = string.IsNullOrWhiteSpace(workOrderId) ? null : workOrderId.Trim(),
            ProductId = productId.Trim(),
            Qty = qty,
            State = LotState.Created,
            ProcessState = LotProcessState.Idle
        };
        lot._routeSteps.AddRange(steps);
        lot.SetAudit(createdBy);
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
        string? trackOutUser, DateTime? trackOutTime)
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
            TrackOutTime = trackOutTime
        };
        lot._routeSteps.AddRange(routeSteps);
        return lot;
    }

    /// <summary>설계 19.4.4 — Hold Lot 거부, Queued에서만 진입, 설비/Recipe/작업자/시각 설정.</summary>
    public Result TrackIn(string equipmentId, string? recipeDefId, int? recipeDefVersion, string user, DateTime serverTime)
    {
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
        TrackInUser = user;
        TrackInTime = serverTime;
        TrackOutUser = null;
        TrackOutTime = null;
        UpdateAudit(user);
        return Result.Success();
    }

    /// <summary>
    /// 설계 19.4.5 — Processing에서만, TrackIn 설비와 일치해야 하며 수량은 Lot Qty 범위 내.
    /// 성공 시 설비/Recipe 점유를 반납하고(현행 TrackOutLotService) 다음 공정으로 이동하거나 완료한다.
    /// </summary>
    public Result TrackOut(string equipmentId, decimal qty, decimal defectQty, string? carrierId, string user, DateTime serverTime)
    {
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 TrackOut할 수 없습니다."));
        if (State != LotState.Processing)
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 TrackOut할 수 없습니다."));
        if (!string.Equals(EquipmentId, equipmentId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure(Error.Conflict("TrackIn 설비와 TrackOut 설비가 일치해야 합니다."));
        if (qty < 0 || defectQty < 0)
            return Result.Failure(Error.Validation(nameof(qty), "수량과 불량수량은 음수일 수 없습니다."));
        if (qty > Qty)
            return Result.Failure(Error.Validation(nameof(qty), "생산수량은 Lot 수량을 초과할 수 없습니다."));
        if (defectQty > qty)
            return Result.Failure(Error.Validation(nameof(defectQty), "불량수량은 생산수량을 초과할 수 없습니다."));

        Qty = qty;
        DefectQty += defectQty;
        CarrierId = string.IsNullOrWhiteSpace(carrierId) ? CarrierId : carrierId.Trim();
        TrackOutUser = user;
        TrackOutTime = serverTime;
        // 현행 TrackOutLotService: TrackOut 시 설비/Recipe 점유 반납
        EquipmentId = null;
        RecipeDefId = null;
        RecipeDefVersion = null;
        ProcessState = LotProcessState.Idle;

        if (IsLastStep)
        {
            State = LotState.Completed;
        }
        else
        {
            CurrentStepIndex++;
            State = LotState.Queued;
        }
        UpdateAudit(user);
        return Result.Success();
    }

    /// <summary>기존 Mixing 출력 Lot에 투입 합계를 가산 (설계 19.4.7 '기존 Mixing Lot 조회' 적응).</summary>
    public Result IncreaseMixingQty(decimal qty, string user)
    {
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 수량을 변경할 수 없습니다."));
        if (State != LotState.Queued)
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 Mixing 수량을 가산할 수 없습니다."));
        if (qty <= 0)
            return Result.Failure(Error.Validation(nameof(qty), "가산 수량은 0보다 커야 합니다."));

        Qty += qty;
        UpdateAudit(user);
        return Result.Success();
    }

    /// <summary>Mixing 투입 소비 — Created/Queued에서만 가능 (설계 19.4.7).
    /// <para>의도된 '원자적 소비': 투입 Lot은 수량과 무관하게 전체가 Consumed로 전이된다(잔량 분할 없음).
    /// 투입량(inQty)은 LotMixingRelation/LotHistory에 genealogy(추적성/수율)로 기록될 뿐 재고 차감이 아니다.
    /// Lot 모델은 부분소비 상태/잔량 개념이 없는 원자 단위로 설계됐다(2026-06-14 결정: 현행 유지).
    /// 부분소비가 필요해지면 도메인 모델(상태·잔량) 확장이 선행돼야 하며 단순 수량 차감으로 바꾸지 말 것.</para></summary>
    public Result Consume(string user)
    {
        if (IsHold)
            return Result.Failure(Error.Conflict("Hold 상태 Lot은 소비할 수 없습니다."));
        if (!LotStateMachine.CanTransition(State, LotState.Consumed))
            return Result.Failure(Error.Conflict($"Lot 상태 {State}에서는 Mixing 투입으로 소비할 수 없습니다."));

        State = LotState.Consumed;
        ProcessState = LotProcessState.Idle;
        UpdateAudit(user);
        return Result.Success();
    }

    public Result Hold(string user)
    {
        if (State is LotState.Completed or LotState.Consumed)
            return Result.Failure(Error.Conflict("종결된 Lot은 Hold할 수 없습니다."));
        IsHold = true;
        UpdateAudit(user);
        return Result.Success();
    }

    public Result ReleaseHold(string user)
    {
        IsHold = false;
        UpdateAudit(user);
        return Result.Success();
    }
}
