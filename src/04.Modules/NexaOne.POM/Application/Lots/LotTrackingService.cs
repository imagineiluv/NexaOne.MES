using NexaOne.Common;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.Lots;

public sealed record TrackInCommand(
    string PlantId, string LotId, string EquipmentId,
    string? RecipeDefId, int? RecipeDefVersion, string User);

public sealed record DefectEntry(string DefectCode, decimal DefectQty);

public sealed record TrackOutCommand(
    string PlantId, string LotId, string EquipmentId, decimal Qty,
    IReadOnlyList<DefectEntry>? Defects, string? CarrierId, string User);

public sealed record MixingInput(string LotId, decimal InQty);

public sealed record MixingTrackCommand(
    string PlantId, string OutputLotId, string ProductId, string EquipmentId,
    IReadOnlyList<string> OutputRouteSteps, IReadOnlyList<MixingInput> Inputs, string User);

public sealed record LotRouteView(
    Lot Lot, IReadOnlyList<LotHistory> Histories, IReadOnlyList<LotMixingRelation> MixingInputs);

/// <summary>
/// Lot TrackIn/TrackOut 생산 추적 (설계서 19.4, 현행 TrackInLot/TrackOutLot/MixingLotTrackInOut Rule).
/// 검증을 모두 통과한 뒤에만 상태를 변경하며, 시각은 항상 서버 시각을 사용한다.
/// 이력은 모든 전이마다 POM_LOT_HISTORY에 추가 기록한다 (현행 LotTraceService).
/// </summary>
public sealed class LotTrackingService
{
    /// <summary>보고서 기본/최대 행 수 — 커서 페이지네이션 대신 상한으로 적응 (설계 19.4.8).</summary>
    public const int DefaultReportRows = 1000;
    public const int MaxReportRows = 5000;

    private readonly ILotRepository _lots;
    private readonly ILotHistoryRepository _histories;
    private readonly ILotMixingRelationRepository _mixings;
    private readonly IProductionOrderRepository _orders;
    private readonly ITrackingMasterGateway _master;

    public LotTrackingService(
        ILotRepository lots,
        ILotHistoryRepository histories,
        ILotMixingRelationRepository mixings,
        IProductionOrderRepository orders,
        ITrackingMasterGateway master)
    {
        _lots = lots;
        _histories = histories;
        _mixings = mixings;
        _orders = orders;
        _master = master;
    }

    // ── 조회 ─────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<Lot>>> GetLotsAsync(
        string plantId, string? state = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<IReadOnlyList<Lot>>(Error.Validation(nameof(plantId), "Plant ID is required."));
        var list = await _lots.GetByPlantAsync(plantId.Trim(), state, ct);
        return Result.Success(list);
    }

    public async Task<Result<LotRouteView>> GetRouteAsync(string lotId, CancellationToken ct = default)
    {
        var lot = await _lots.GetByIdAsync(lotId, ct);
        if (lot is null)
            return Result.Failure<LotRouteView>(Error.NotFound(nameof(Lot), lotId));

        var histories = await _histories.GetByLotAsync(lot.PlantId, lot.Id, ct);
        var mixings = await _mixings.GetByOutputLotAsync(lot.PlantId, lot.Id, ct);
        return Result.Success(new LotRouteView(lot, histories, mixings));
    }

    public async Task<Result<IReadOnlyList<LotHistory>>> GetTrackingReportAsync(
        string plantId, string? lotId, string? equipmentId, string? processId,
        DateTime? from, DateTime? to, int maxRows = DefaultReportRows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<IReadOnlyList<LotHistory>>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (from.HasValue && to.HasValue && from > to)
            return Result.Failure<IReadOnlyList<LotHistory>>(Error.Validation(nameof(from), "조회 시작이 종료보다 늦을 수 없습니다."));

        var rows = Math.Clamp(maxRows, 1, MaxReportRows);
        var list = await _histories.SearchAsync(
            plantId.Trim(), Trimmed(lotId), Trimmed(equipmentId), Trimmed(processId), from, to, rows, ct);
        return Result.Success(list);
    }

    // ── Lot 생성 ──────────────────────────────────────────────────────────────

    public async Task<Result<Lot>> CreateLotAsync(
        string plantId, string lotId, string? workOrderId, string productId,
        decimal qty, IReadOnlyList<string> routeSteps, string user, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(lotId) && await _lots.GetByIdAsync(lotId.Trim(), ct) is not null)
            return Result.Failure<Lot>(Error.Conflict($"Lot '{lotId.Trim()}'은(는) 이미 존재합니다."));

        if (!string.IsNullOrWhiteSpace(workOrderId))
        {
            var order = await _orders.GetByIdAsync(workOrderId.Trim(), ct);
            if (order is null)
                return Result.Failure<Lot>(Error.NotFound("ProductionOrder", workOrderId.Trim()));
            if (!string.Equals(order.ProductId, productId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Validation(nameof(productId), "Lot 제품이 생산 지시 제품과 일치해야 합니다."));
        }

        var result = Lot.Create(lotId!, plantId, workOrderId, productId!, qty, routeSteps, user);
        if (result.IsFailure) return result;

        await _lots.AddAsync(result.Value, ct);
        return result;
    }

    // ── TrackIn (설계 19.4.4) ────────────────────────────────────────────────

    public async Task<Result<Lot>> TrackInAsync(TrackInCommand command, CancellationToken ct = default)
    {
        // 1. Lot 존재 및 Plant 일치
        var lot = await _lots.GetByIdAsync(command.LotId, ct);
        if (lot is null)
            return Result.Failure<Lot>(Error.NotFound(nameof(Lot), command.LotId));
        if (!string.Equals(lot.PlantId, command.PlantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Lot>(Error.Validation(nameof(command.PlantId), "Lot의 Plant와 요청 Plant가 일치하지 않습니다."));

        // 2~3. 상태/Hold — 설비 조회 전에 차단 (설계 검증 순서 유지)
        if (lot.IsHold)
            return Result.Failure<Lot>(Error.Conflict("Hold 상태 Lot은 TrackIn할 수 없습니다."));
        if (lot.State != LotState.Queued)
            return Result.Failure<Lot>(Error.Conflict($"Lot 상태 {lot.State}에서는 TrackIn할 수 없습니다."));

        // 4. 설비 존재/Plant 일치/사용 가능
        var equipment = await ValidateEquipmentAsync(command.EquipmentId, command.PlantId!, ct);
        if (equipment.IsFailure)
            return Result.Failure<Lot>(equipment.Error);

        // 6. Recipe 검증 — 미지정은 허용 (현행 setIsUseValidationRecipe(false) 설비의 적응)
        if (!string.IsNullOrWhiteSpace(command.RecipeDefId))
        {
            var usable = await _master.IsUsableRecipeAsync(
                command.RecipeDefId.Trim(), command.RecipeDefVersion, equipment.Value.EquipmentClassId, ct);
            if (!usable)
                return Result.Failure<Lot>(Error.Validation(
                    nameof(command.RecipeDefId), "배포(Released) 상태이고 설비 클래스가 일치하는 Recipe가 아닙니다."));
        }

        // 8. TRACK_IN_TIME은 서버 시각
        var serverTime = DateTime.UtcNow;
        var trackIn = lot.TrackIn(command.EquipmentId, command.RecipeDefId, command.RecipeDefVersion, command.User, serverTime);
        if (trackIn.IsFailure)
            return Result.Failure<Lot>(trackIn.Error);

        await _lots.UpdateAsync(lot, ct);
        await _histories.AddAsync(LotHistory.Of(lot, LotExecutionId.TrackIn, command.User, lot.Qty, 0), ct);

        // 작업지시 Confirm(Issued) -> Start (설계 19.4.4) — 실패해도 TrackIn 자체는 유지 (수동 시작 가능)
        if (lot.WorkOrderId is not null)
        {
            var order = await _orders.GetByIdAsync(lot.WorkOrderId, ct);
            if (order is not null && order.Status == ProductionOrderStatus.Issued)
            {
                var started = order.Start(serverTime);
                if (started.IsSuccess)
                    await _orders.UpdateAsync(order, ct);
            }
        }
        return Result.Success(lot);
    }

    // ── TrackOut (설계 19.4.5) ───────────────────────────────────────────────

    public async Task<Result<Lot>> TrackOutAsync(TrackOutCommand command, CancellationToken ct = default)
    {
        // 1. Lot 존재 및 Plant 일치
        var lot = await _lots.GetByIdAsync(command.LotId, ct);
        if (lot is null)
            return Result.Failure<Lot>(Error.NotFound(nameof(Lot), command.LotId));
        if (!string.Equals(lot.PlantId, command.PlantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Lot>(Error.Validation(nameof(command.PlantId), "Lot의 Plant와 요청 Plant가 일치하지 않습니다."));

        // 6. 불량 코드 유효성 — 상태 변경 전에 전부 검증
        var defects = command.Defects ?? [];
        if (defects.Any(d => d.DefectQty < 0))
            return Result.Failure<Lot>(Error.Validation("defects", "불량수량은 음수일 수 없습니다."));
        if (defects.Select(d => d.DefectCode?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count() != defects.Count)
            return Result.Failure<Lot>(Error.Validation("defects", "불량 코드가 중복되었습니다."));
        foreach (var defect in defects)
        {
            if (string.IsNullOrWhiteSpace(defect.DefectCode) ||
                !await _master.IsValidDefectCodeAsync(defect.DefectCode.Trim(), ct))
                return Result.Failure<Lot>(Error.Validation("defects", $"유효하지 않은 불량 코드입니다: '{defect.DefectCode}'"));
        }
        var totalDefectQty = defects.Sum(d => d.DefectQty);

        // 이력에는 반납 전 설비/Recipe/공정을 기록한다 — TrackOut이 점유를 비우기 때문
        var equipmentBefore = lot.EquipmentId;
        var recipeBefore = lot.RecipeDefId;
        var recipeVersionBefore = lot.RecipeDefVersion;
        var processBefore = lot.CurrentProcessId;
        var trackInTimeBefore = lot.TrackInTime;

        // 2~5, 7. 상태/설비 일치/Hold/수량 범위 + 서버 시각 — 도메인에서 검증
        var serverTime = DateTime.UtcNow;
        var trackOut = lot.TrackOut(command.EquipmentId, command.Qty, totalDefectQty, command.CarrierId, command.User, serverTime);
        if (trackOut.IsFailure)
            return Result.Failure<Lot>(trackOut.Error);

        await _lots.UpdateAsync(lot, ct);
        await _histories.AddAsync(new LotHistory(
            0, lot.PlantId, lot.Id, equipmentBefore, processBefore,
            recipeBefore, recipeVersionBefore, trackInTimeBefore, lot.TrackOutTime,
            LotExecutionId.TrackOut, command.User, lot.Qty, totalDefectQty,
            lot.State.ToString(), lot.ProcessState.ToString(), serverTime), ct);

        if (lot.State == LotState.Completed)
        {
            await _histories.AddAsync(LotHistory.Of(lot, LotExecutionId.Finish, command.User, lot.Qty, lot.DefectQty), ct);
            await TryFinishWorkOrderAsync(lot, serverTime, ct);
        }
        return Result.Success(lot);
    }

    /// <summary>
    /// 같은 작업지시의 진행 중 Lot이 없으면 W/O Finish (설계 19.4.5).
    /// 자동 마감은 best-effort — 실패해도 TrackOut은 유지하고 화면에서 수동 완료할 수 있다.
    /// </summary>
    private async Task TryFinishWorkOrderAsync(Lot lot, DateTime serverTime, CancellationToken ct)
    {
        if (lot.WorkOrderId is null) return;

        var siblings = await _lots.GetByWorkOrderAsync(lot.WorkOrderId, ct);
        if (siblings.Any(l => l.State is not (LotState.Completed or LotState.Consumed)))
            return;

        var order = await _orders.GetByIdAsync(lot.WorkOrderId, ct);
        if (order is null || order.Status != ProductionOrderStatus.InProgress) return;

        var actualQty = siblings.Where(l => l.State == LotState.Completed).Sum(l => l.Qty);
        if (order.Complete(actualQty, serverTime).IsSuccess)
            await _orders.UpdateAsync(order, ct);
    }

    // ── Mixing TrackIn/TrackOut (설계 19.4.7) ────────────────────────────────

    public async Task<Result<Lot>> MixingTrackInOutAsync(MixingTrackCommand command, CancellationToken ct = default)
    {
        var plantId = command.PlantId?.Trim() ?? string.Empty;
        if (plantId.Length == 0)
            return Result.Failure<Lot>(Error.Validation(nameof(command.PlantId), "Plant ID is required."));
        if (command.Inputs is null || command.Inputs.Count == 0)
            return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), "투입 Lot 목록이 비어 있습니다."));
        if (command.Inputs.Any(i => i.InQty <= 0))
            return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), "투입 수량은 0보다 커야 합니다."));
        if (command.Inputs.Select(i => i.LotId?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != command.Inputs.Count)
            return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), "투입 Lot이 중복되었습니다."));

        // 입력 Lot 전수 검증 — 상태 변경 전에 모두 통과해야 한다 (UnitOfWork 부재 적응)
        var inputs = new List<(Lot Lot, decimal InQty)>();
        foreach (var input in command.Inputs)
        {
            var lot = await _lots.GetByIdAsync(input.LotId?.Trim() ?? string.Empty, ct);
            if (lot is null)
                return Result.Failure<Lot>(Error.NotFound(nameof(Lot), input.LotId ?? string.Empty));
            if (!string.Equals(lot.PlantId, plantId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), $"투입 Lot '{lot.Id}'의 Plant가 일치하지 않습니다."));
            if (lot.IsHold)
                return Result.Failure<Lot>(Error.Conflict($"투입 Lot '{lot.Id}'이(가) Hold 상태입니다."));
            if (!LotStateMachine.CanTransition(lot.State, LotState.Consumed))
                return Result.Failure<Lot>(Error.Conflict($"투입 Lot '{lot.Id}'의 상태 {lot.State}에서는 소비할 수 없습니다."));
            if (input.InQty > lot.Qty)
                return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), $"투입 수량이 Lot '{lot.Id}' 수량을 초과합니다."));
            inputs.Add((lot, input.InQty));
        }
        var totalQty = inputs.Sum(i => i.InQty);

        // 설비 검증 (TrackIn과 동일 기준)
        var equipment = await ValidateEquipmentAsync(command.EquipmentId, plantId, ct);
        if (equipment.IsFailure)
            return Result.Failure<Lot>(equipment.Error);

        // 출력 Lot 생성 또는 기존 Mixing Lot 사용 (설계 19.4.7)
        var output = await _lots.GetByIdAsync(command.OutputLotId?.Trim() ?? string.Empty, ct);
        var isNewOutput = output is null;
        if (output is null)
        {
            var created = Lot.Create(
                command.OutputLotId ?? string.Empty, plantId, workOrderId: null,
                command.ProductId, totalQty, command.OutputRouteSteps, command.User);
            if (created.IsFailure)
                return created;
            output = created.Value;
        }
        else
        {
            if (!string.Equals(output.PlantId, plantId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Validation(nameof(command.OutputLotId), "출력 Lot의 Plant가 일치하지 않습니다."));
            var increased = output.IncreaseMixingQty(totalQty, command.User);
            if (increased.IsFailure)
                return Result.Failure<Lot>(increased.Error);
        }

        var serverTime = DateTime.UtcNow;

        // 입력 Lot 소비 + 관계 기록
        foreach (var (lot, inQty) in inputs)
        {
            var consumed = lot.Consume(command.User);
            if (consumed.IsFailure)
                return Result.Failure<Lot>(consumed.Error);
            await _lots.UpdateAsync(lot, ct);
            await _histories.AddAsync(LotHistory.Of(lot, LotExecutionId.Consume, command.User, inQty, 0), ct);
            await _mixings.AddAsync(new LotMixingRelation(
                plantId, output.Id, lot.Id, inQty,
                totalQty == 0 ? null : Math.Round(inQty / totalQty, 4), serverTime, command.User), ct);
        }

        // 출력 Lot 저장 후 TrackIn -> TrackOut 연속 수행
        if (isNewOutput) await _lots.AddAsync(output, ct);
        else await _lots.UpdateAsync(output, ct);

        var trackIn = output.TrackIn(command.EquipmentId, null, null, command.User, serverTime);
        if (trackIn.IsFailure)
            return Result.Failure<Lot>(trackIn.Error);
        await _lots.UpdateAsync(output, ct);
        await _histories.AddAsync(LotHistory.Of(output, LotExecutionId.TrackIn, command.User, output.Qty, 0), ct);

        var equipmentBefore = output.EquipmentId;
        var processBefore = output.CurrentProcessId;
        var trackInTimeBefore = output.TrackInTime;
        var trackOut = output.TrackOut(command.EquipmentId, output.Qty, 0, null, command.User, DateTime.UtcNow);
        if (trackOut.IsFailure)
            return Result.Failure<Lot>(trackOut.Error);
        await _lots.UpdateAsync(output, ct);
        await _histories.AddAsync(new LotHistory(
            0, output.PlantId, output.Id, equipmentBefore, processBefore,
            null, null, trackInTimeBefore, output.TrackOutTime,
            LotExecutionId.TrackOut, command.User, output.Qty, 0,
            output.State.ToString(), output.ProcessState.ToString(), DateTime.UtcNow), ct);
        if (output.State == LotState.Completed)
            await _histories.AddAsync(LotHistory.Of(output, LotExecutionId.Finish, command.User, output.Qty, output.DefectQty), ct);

        return Result.Success(output);
    }

    // ── Hold / Release ───────────────────────────────────────────────────────

    public async Task<Result> HoldAsync(string lotId, string user, CancellationToken ct = default)
    {
        var lot = await _lots.GetByIdAsync(lotId, ct);
        if (lot is null)
            return Result.Failure(Error.NotFound(nameof(Lot), lotId));

        var held = lot.Hold(user);
        if (held.IsFailure) return held;

        await _lots.UpdateAsync(lot, ct);
        return Result.Success();
    }

    public async Task<Result> ReleaseHoldAsync(string lotId, string user, CancellationToken ct = default)
    {
        var lot = await _lots.GetByIdAsync(lotId, ct);
        if (lot is null)
            return Result.Failure(Error.NotFound(nameof(Lot), lotId));

        var released = lot.ReleaseHold(user);
        if (released.IsFailure) return released;

        await _lots.UpdateAsync(lot, ct);
        return Result.Success();
    }

    // ── 내부 헬퍼 ─────────────────────────────────────────────────────────────

    private async Task<Result<TrackingEquipmentInfo>> ValidateEquipmentAsync(
        string equipmentId, string plantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<TrackingEquipmentInfo>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));

        var equipment = await _master.GetEquipmentAsync(equipmentId.Trim(), ct);
        if (equipment is null)
            return Result.Failure<TrackingEquipmentInfo>(Error.NotFound("Equipment", equipmentId.Trim()));
        if (!string.Equals(equipment.PlantId, plantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<TrackingEquipmentInfo>(Error.Validation(nameof(equipmentId), "설비의 Plant가 일치하지 않습니다."));
        if (!equipment.IsValid)
            return Result.Failure<TrackingEquipmentInfo>(Error.Conflict("사용 가능 상태가 아닌 설비입니다."));
        return Result.Success(equipment);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
