using System.Globalization;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.POM.Application.Lots;

public sealed record TrackInCommand(
    string PlantId, string LotId, string EquipmentId,
    string? RecipeDefId, int? RecipeDefVersion, string User,
    int ExpectedVersion, string IdempotencyKey,
    string ClientChannel = "MES", string? DeviceId = null);

public sealed record DefectEntry(string DefectCode, decimal DefectQty);

public sealed record TrackOutCommand(
    string PlantId, string LotId, string EquipmentId, decimal Qty,
    IReadOnlyList<DefectEntry>? Defects, string? CarrierId, string User,
    int ExpectedVersion, string IdempotencyKey,
    string ClientChannel = "MES", string? DeviceId = null);

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
    /// <summary>일회성 라우팅 승인이 장기 우회권한으로 변질되지 않게 제한하는 최대 유효시간.</summary>
    public static readonly TimeSpan MaximumRouteExceptionLifetime = TimeSpan.FromHours(8);

    private readonly ILotRepository _lots;
    private readonly IAtomicLotRepository _atomicLots;
    private readonly ILotHistoryRepository _histories;
    private readonly ILotMixingRelationRepository _mixings;
    private readonly IPomWorkOrderRepository _workOrders;
    private readonly ITrackingMasterGateway _master;
    private readonly IProductionQualityGateway _productionQuality;
    private readonly IRoutingPolicyEvaluator _routingPolicy;

    public LotTrackingService(
        ILotRepository lots,
        IAtomicLotRepository atomicLots,
        ILotHistoryRepository histories,
        ILotMixingRelationRepository mixings,
        IPomWorkOrderRepository workOrders,
        ITrackingMasterGateway master,
        IProductionQualityGateway productionQuality)
        : this(lots, atomicLots, histories, mixings, workOrders, master, productionQuality, new RoutingPolicyEvaluator())
    {
    }

    /// <summary>업종별 라우팅 정책 드라이버를 주입할 수 있는 확장 생성자다.</summary>
    public LotTrackingService(
        ILotRepository lots,
        IAtomicLotRepository atomicLots,
        ILotHistoryRepository histories,
        ILotMixingRelationRepository mixings,
        IPomWorkOrderRepository workOrders,
        ITrackingMasterGateway master,
        IProductionQualityGateway productionQuality,
        IRoutingPolicyEvaluator routingPolicy)
    {
        _lots = lots;
        _atomicLots = atomicLots ?? throw new ArgumentNullException(nameof(atomicLots));
        _histories = histories;
        _mixings = mixings;
        _workOrders = workOrders;
        _master = master;
        _productionQuality = productionQuality;
        _routingPolicy = routingPolicy ?? throw new ArgumentNullException(nameof(routingPolicy));
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
            return Result.Failure<LotRouteView>(Error.NotFoundOf(nameof(Lot), lotId));

        var histories = await _histories.GetByLotAsync(lot.PlantId, lot.Id, ct);
        var mixings = await _mixings.GetByOutputLotAsync(lot.PlantId, lot.Id, ct);
        return Result.Success(new LotRouteView(lot, histories, mixings));
    }

    /// <summary>현재 LOT과 승인 원장을 묶어 라우팅 진행·복귀 상태를 조회한다.</summary>
    public async Task<Result<LotRoutingContext>> GetRoutingContextAsync(
        string lotId, CancellationToken ct = default)
    {
        var lot = await _lots.GetByIdAsync(lotId?.Trim() ?? string.Empty, ct);
        if (lot is null)
            return Result.Failure<LotRoutingContext>(Error.NotFoundOf(nameof(Lot), lotId ?? string.Empty));

        IReadOnlyList<RouteExceptionRequest> exceptions = Array.Empty<RouteExceptionRequest>();
        if (_lots is IRouteExceptionRepository repository)
        {
            var stored = await repository.GetRouteExceptionsByLotAsync(lot.Id, ct);
            var projected = new List<RouteExceptionRequest>(stored.Count);
            foreach (var request in stored)
                projected.Add(ProjectExpirationForRead(request, DateTime.UtcNow));
            exceptions = projected;
        }
        return Result.Success(new LotRoutingContext(
            lot, lot.ReturnStepIndex, lot.ReturnProcessId, exceptions));
    }

    /// <summary>LOT을 변경하지 않고 공정 이탈의 차단·경고·승인 필요 여부를 평가한다.</summary>
    public async Task<Result<RoutingPolicyDecision>> EvaluateRoutingAsync(
        EvaluateRoutingCommand command, CancellationToken ct = default)
    {
        var lotResult = await LoadLotForPlantAsync(command.LotId, command.PlantId, ct);
        if (lotResult.IsFailure)
            return Result.Failure<RoutingPolicyDecision>(lotResult.Error);

        RouteExceptionRequest? exception = null;
        if (!string.IsNullOrWhiteSpace(command.ExceptionId))
        {
            if (_lots is not IRouteExceptionRepository repository)
                return Result.Failure<RoutingPolicyDecision>(Error.Conflict(
                    "The configured lot repository does not support route exceptions."));
            exception = await repository.GetRouteExceptionAsync(command.ExceptionId.Trim(), ct);
            if (exception is null)
                return Result.Failure<RoutingPolicyDecision>(
                    Error.NotFoundOf(nameof(RouteExceptionRequest), command.ExceptionId.Trim()));
            exception = ProjectExpirationForRead(exception, DateTime.UtcNow);
        }

        return Result.Success(_routingPolicy.Evaluate(
            lotResult.Value, command.DeviationType, command.TargetStepIndex,
            command.Reason, exception, DateTime.UtcNow));
    }

    /// <summary>LOT별 유효 라우팅 통제 모드를 사유·버전·멱등 실행 이력과 함께 변경한다.</summary>
    public async Task<Result<Lot>> ChangeRoutingControlModeAsync(
        ChangeRoutingControlModeCommand command, CancellationToken ct = default)
    {
        if (!IsSupportedChannel(command.ClientChannel))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var auditInput = ValidateRoutingAuditInput(command.Reason, command.DeviceId);
        if (auditInput.IsFailure) return Result.Failure<Lot>(auditInput.Error);
        if (string.IsNullOrWhiteSpace(command.User))
            return Result.Failure<Lot>(Error.Validation(nameof(command.User), "User is required."));
        if (command.User.Trim().Length > PomStorageBoundary.ActorLength)
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.User), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var user = command.User.Trim();

        var lotResult = await LoadLotForPlantAsync(command.LotId, command.PlantId, ct);
        if (lotResult.IsFailure) return Result.Failure<Lot>(lotResult.Error);
        var lot = lotResult.Value;
        var requestHash = HashRequest(
            LotExecutionId.RoutingModeChange, lot.Id, command.PlantId,
            command.ControlMode, command.Reason, user,
            command.ClientChannel, command.DeviceId);
        var transition = await PrepareTransitionAsync(
            lot, LotExecutionId.RoutingModeChange, command.ExpectedVersion,
            command.IdempotencyKey, requestHash, ct);
        if (transition.IsFailure) return Result.Failure<Lot>(transition.Error);
        if (transition.Value.IsReplay) return Result.Success(lot);

        var previousMode = lot.ControlMode;
        var changed = lot.ChangeRoutingControlMode(command.ControlMode, command.Reason, user);
        if (changed.IsFailure) return Result.Failure<Lot>(changed.Error);

        var history = LotHistory.Of(
            lot, LotExecutionId.RoutingModeChange, user, lot.Qty, lot.DefectQty) with
        {
            Reason = PomStorageBoundary.HistorySummary(
                $"{previousMode}->{command.ControlMode}: ", command.Reason),
            IdempotencyKey = transition.Value.IdempotencyKey
        };
        var audit = new RoutingTransitionAudit(
            lot.CurrentStepIndex, lot.CurrentStepIndex,
            lot.CurrentProcessId, lot.CurrentProcessId, lot.ControlMode, null,
            NormalizeChannel(command.ClientChannel), Trimmed(command.DeviceId), command.Reason.Trim());
        var persisted = await PersistTransitionAsync(
            lot, transition.Value, [history], null, null, ct, routingAudit: audit);
        return persisted
            ? Result.Success(lot)
            : Result.Failure<Lot>(Error.Conflict("Lot was changed by another request."));
    }

    /// <summary>Flexible 모드에서 현재 LOT 버전과 출발·도착 공정에 묶인 예외 승인을 요청한다.</summary>
    public async Task<Result<RouteExceptionRequest>> RequestRouteExceptionAsync(
        RequestRouteExceptionCommand command, CancellationToken ct = default)
    {
        if (_lots is not IRouteExceptionRepository repository)
            return Result.Failure<RouteExceptionRequest>(Error.Conflict(
                "The configured lot repository does not support route exceptions."));
        if (!IsSupportedChannel(command.ClientChannel))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (!PomStorageBoundary.FitsRequired(command.User, PomStorageBoundary.ActorLength))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.User),
                $"Requester is required and cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (!PomStorageBoundary.FitsRequired(command.ExceptionId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.ExceptionId),
                $"Exception ID is required and cannot exceed {PomStorageBoundary.IdentifierLength} characters."));
        var requestAuditInput = ValidateRoutingAuditInput(command.Reason, command.DeviceId);
        if (requestAuditInput.IsFailure)
            return Result.Failure<RouteExceptionRequest>(requestAuditInput.Error);

        // Idempotent replay is resolved from the immutable ledger before consulting mutable LOT
        // state. Plant/Lot are part of the identity so this early lookup cannot expose another boundary.
        var existing = await repository.GetRouteExceptionAsync(command.ExceptionId?.Trim() ?? string.Empty, ct);
        if (existing is not null)
        {
            return SameRouteExceptionRequest(existing, command)
                ? Result.Success(ProjectExpirationForRead(existing, DateTime.UtcNow))
                : Result.Failure<RouteExceptionRequest>(Error.Conflict(
                    $"Route exception ID '{command.ExceptionId}' is already used for a different request."));
        }

        var lotResult = await LoadLotForPlantAsync(command.LotId, command.PlantId, ct);
        if (lotResult.IsFailure) return Result.Failure<RouteExceptionRequest>(lotResult.Error);
        var lot = lotResult.Value;
        if (lot.VersionNo != command.ExpectedVersion)
            return Result.Failure<RouteExceptionRequest>(Error.Conflict(
                $"Lot version conflict. Expected {command.ExpectedVersion}, current {lot.VersionNo}."));
        if (lot.ControlMode != RoutingControlMode.Flexible)
            return Result.Failure<RouteExceptionRequest>(Error.Conflict(
                "Route exception approval requests are available only in Flexible mode."));
        if (command.DeviationType is RouteDeviationType.Normal or RouteDeviationType.Return)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.DeviationType),
                "Normal routing and automatic rework Return cannot be requested as exceptions."));

        var structural = lot.ValidateRouteDeviation(command.DeviationType, command.TargetStepIndex);
        if (structural.IsFailure)
            return Result.Failure<RouteExceptionRequest>(structural.Error);

        var requestedAt = DateTime.UtcNow;
        var requestedExpiry = NormalizeUtc(command.ExpiresAt);
        if (requestedExpiry <= requestedAt)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.ExpiresAt), "Route exception expiry must be in the future."));

        // The server owns the effective expiry. A client may request a shorter lifetime, but a
        // longer timestamp is clamped to the eight-hour safety limit and is not part of retry identity.
        var expiresAt = requestedExpiry < requestedAt + MaximumRouteExceptionLifetime
            ? requestedExpiry
            : requestedAt + MaximumRouteExceptionLifetime;

        var requested = RouteExceptionRequest.Request(
            command.ExceptionId ?? string.Empty, lot.Id, lot.PlantId, command.DeviationType,
            lot.CurrentStepIndex, command.TargetStepIndex,
            lot.CurrentProcessId, lot.RouteSteps[command.TargetStepIndex], command.ExpectedVersion,
            command.Reason ?? string.Empty, command.User ?? string.Empty,
            requestedAt, expiresAt, command.ClientChannel, command.DeviceId);
        if (requested.IsFailure) return requested;

        var addResult = await repository.TryAddRouteExceptionAsync(requested.Value, ct);
        if (addResult == RouteExceptionAddResult.Added)
            return requested;
        if (addResult != RouteExceptionAddResult.AlreadyExists)
            throw new InvalidOperationException($"Unknown route exception add result '{addResult}'.");

        // Two callers can pass the initial lookup concurrently. Reload the unique key so an exact
        // retry succeeds, while a genuinely different request receives a 409-style result.
        var concurrent = await repository.GetRouteExceptionAsync(requested.Value.Id, ct);
        if (concurrent is null)
            throw new InvalidOperationException(
                $"Route exception '{requested.Value.Id}' was reported as existing but could not be loaded.");
        return SameRouteExceptionRequest(concurrent, command)
            ? Result.Success(ProjectExpirationForRead(concurrent, DateTime.UtcNow))
            : Result.Failure<RouteExceptionRequest>(Error.Conflict(
                $"Route exception ID '{command.ExceptionId}' was created concurrently for a different request."));
    }

    /// <summary>요청자와 다른 검토자가 유효기간 안의 예외 요청을 승인한다.</summary>
    public Task<Result<RouteExceptionRequest>> ApproveRouteExceptionAsync(
        ReviewRouteExceptionCommand command, CancellationToken ct = default)
        => ReviewRouteExceptionAsync(command, approve: true, ct);

    /// <summary>대기 중인 예외 요청을 필수 반려 사유와 함께 종료한다.</summary>
    public Task<Result<RouteExceptionRequest>> RejectRouteExceptionAsync(
        ReviewRouteExceptionCommand command, CancellationToken ct = default)
        => ReviewRouteExceptionAsync(command, approve: false, ct);

    /// <summary>예외 요청 ID로 현재 승인 원장 상태를 조회한다.</summary>
    public async Task<Result<RouteExceptionRequest>> GetRouteExceptionAsync(
        string exceptionId, CancellationToken ct = default)
    {
        if (_lots is not IRouteExceptionRepository repository)
            return Result.Failure<RouteExceptionRequest>(Error.Conflict(
                "The configured lot repository does not support route exceptions."));
        var request = await repository.GetRouteExceptionAsync(exceptionId?.Trim() ?? string.Empty, ct);
        if (request is null)
            return Result.Failure<RouteExceptionRequest>(
                Error.NotFoundOf(nameof(RouteExceptionRequest), exceptionId ?? string.Empty));
        return Result.Success(ProjectExpirationForRead(request, DateTime.UtcNow));
    }

    /// <summary>
    /// Strict/Flexible/NoControl 판정 후 공정 이탈과 승인 소비, LOT history/execution을 한 트랜잭션에 저장한다.
    /// </summary>
    public async Task<Result<Lot>> ApplyRouteDeviationAsync(
        ApplyRouteDeviationCommand command, CancellationToken ct = default)
    {
        if (!IsSupportedChannel(command.ClientChannel))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var auditInput = ValidateRoutingAuditInput(command.Reason, command.DeviceId);
        if (auditInput.IsFailure) return Result.Failure<Lot>(auditInput.Error);
        if (string.IsNullOrWhiteSpace(command.User))
            return Result.Failure<Lot>(Error.Validation(nameof(command.User), "User is required."));
        if (command.User.Trim().Length > PomStorageBoundary.ActorLength)
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.User), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var user = command.User.Trim();

        var lotResult = await LoadLotForPlantAsync(command.LotId, command.PlantId, ct);
        if (lotResult.IsFailure) return Result.Failure<Lot>(lotResult.Error);
        var lot = lotResult.Value;
        if (command.DeviationType is RouteDeviationType.Normal or RouteDeviationType.Return)
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.DeviationType),
                "Normal routing uses TrackIn/TrackOut, and rework Return is automatic after TrackOut."));

        var requestHash = HashRequest(
            "RouteDeviation", lot.Id, command.PlantId, command.DeviationType,
            command.TargetStepIndex, command.Reason, command.ExceptionId,
            user, command.ClientChannel, command.DeviceId);
        var action = LotExecutionId.For(command.DeviationType);
        var transition = await PrepareTransitionAsync(
            lot, action, command.ExpectedVersion, command.IdempotencyKey, requestHash, ct);
        if (transition.IsFailure) return Result.Failure<Lot>(transition.Error);
        if (transition.Value.IsReplay) return Result.Success(lot);

        RouteExceptionRequest? exception = null;
        if (!string.IsNullOrWhiteSpace(command.ExceptionId))
        {
            if (_lots is not IRouteExceptionRepository repository)
                return Result.Failure<Lot>(Error.Conflict(
                    "The configured lot repository does not support route exceptions."));
            exception = await repository.GetRouteExceptionAsync(command.ExceptionId.Trim(), ct);
            if (exception is null)
                return Result.Failure<Lot>(
                    Error.NotFoundOf(nameof(RouteExceptionRequest), command.ExceptionId.Trim()));
            exception = await PersistExpirationForWriteAsync(
                repository, exception, DateTime.UtcNow, ct);
        }

        var decision = _routingPolicy.Evaluate(
            lot, command.DeviationType, command.TargetStepIndex,
            command.Reason, exception, DateTime.UtcNow);
        if (!decision.IsAllowed)
            return Result.Failure<Lot>(Error.Conflict($"{decision.Code}: {decision.Message}"));

        // Approval mode never relaxes product-quality invariants. A Bypass may skip several
        // process gates, so every skipped process must already be NotRequired or Passed.
        if (command.DeviationType == RouteDeviationType.Bypass)
        {
            var qualityGate = await ValidateBypassQualityGatesAsync(
                lot, command.TargetStepIndex, ct);
            if (qualityGate.IsFailure)
                return Result.Failure<Lot>(qualityGate.Error);
        }

        var fromStep = lot.CurrentStepIndex;
        var fromProcess = lot.CurrentProcessId;
        var equipment = lot.EquipmentId;
        var recipe = lot.RecipeDefId;
        var recipeVersion = lot.RecipeDefVersion;
        var trackInTime = lot.TrackInTime;
        var now = DateTime.UtcNow;
        var executionId = Guid.NewGuid().ToString("N");

        if (exception is not null)
        {
            var applied = exception.MarkApplied(user, executionId, now);
            if (applied.IsFailure)
            {
                if (exception.Status == RouteExceptionStatus.Expired &&
                    _lots is IRouteExceptionRepository repository)
                    await repository.UpdateRouteExceptionAsync(
                        exception, RouteExceptionStatus.Approved, ct);
                return Result.Failure<Lot>(applied.Error);
            }
        }

        var changed = lot.ApplyRouteDeviation(
            command.DeviationType, command.TargetStepIndex,
            command.Reason, user, exception?.Id);
        if (changed.IsFailure) return Result.Failure<Lot>(changed.Error);

        // 실행 중 품질 실패에서 Rework하는 경우에도 출발 공정의 설비·Recipe·TrackIn 증거를 잃지 않는다.
        var history = new LotHistory(
            0, lot.PlantId, lot.Id, equipment, fromProcess,
            recipe, recipeVersion, trackInTime, null,
            action, user, lot.Qty, lot.DefectQty,
            lot.State.ToString(), lot.ProcessState.ToString(), now,
            PomStorageBoundary.HistorySummary(string.Empty, command.Reason),
            transition.Value.IdempotencyKey);
        var audit = new RoutingTransitionAudit(
            fromStep,
            command.DeviationType is RouteDeviationType.Alternative or RouteDeviationType.SequenceChange
                ? lot.CurrentStepIndex
                : command.TargetStepIndex,
            fromProcess,
            lot.CurrentProcessId,
            lot.ControlMode, exception?.Id,
            NormalizeChannel(command.ClientChannel), Trimmed(command.DeviceId), command.Reason.Trim());
        var persisted = await PersistTransitionAsync(
            lot, transition.Value, [history], null, null, ct,
            exception, audit, executionId);
        return persisted
            ? Result.Success(lot)
            : Result.Failure<Lot>(Error.Conflict("Lot or route exception was changed by another request."));
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
        if (!PomStorageBoundary.FitsRequired(lotId, PomStorageBoundary.IdentifierLength))
            return Result.Failure<Lot>(Error.Validation(
                nameof(lotId), $"Lot ID is required and cannot exceed {PomStorageBoundary.IdentifierLength} characters."));
        if (!PomStorageBoundary.FitsRequired(user, PomStorageBoundary.ActorLength))
            return Result.Failure<Lot>(Error.Validation(
                nameof(user), $"User is required and cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (qty <= 0 || !ProductionQuantityBoundary.Fits(qty))
            return Result.Failure<Lot>(Error.Validation(
                nameof(qty), "Lot quantity must be positive and fit DECIMAL(18,4)."));
        if (!string.IsNullOrWhiteSpace(lotId) && await _lots.GetByIdAsync(lotId.Trim(), ct) is not null)
            return Result.Failure<Lot>(Error.Conflict($"Lot '{lotId.Trim()}'은(는) 이미 존재합니다."));

        var normalizedRouteSteps = (routeSteps ?? [])
            .Select(step => step?.Trim())
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .ToList();

        if (!string.IsNullOrWhiteSpace(workOrderId))
        {
            var workOrder = await _workOrders.GetByIdAsync(workOrderId.Trim(), ct);
            if (workOrder is null)
                return Result.Failure<Lot>(Error.NotFoundOf(nameof(PomWorkOrder), workOrderId.Trim()));
            if (workOrder.Status is PomWorkOrderStatus.Completed or PomWorkOrderStatus.Cancelled)
                return Result.Failure<Lot>(Error.Conflict("A lot cannot be added to a terminal work order."));
            if (!string.Equals(workOrder.PlantId, plantId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Validation(nameof(plantId), "Lot plant must match the work order."));
            if (!string.Equals(workOrder.ProductId, productId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Validation(nameof(productId), "Lot 제품이 생산 지시 제품과 일치해야 합니다."));
            if (workOrder.RoutingScope == PomWorkOrderRoutingScope.Operation)
            {
                if (string.IsNullOrWhiteSpace(workOrder.ProcessId) ||
                    normalizedRouteSteps.Count != 1 ||
                    !string.Equals(normalizedRouteSteps[0], workOrder.ProcessId, StringComparison.OrdinalIgnoreCase))
                    return Result.Failure<Lot>(Error.Validation(nameof(routeSteps),
                        "A process work order requires a single matching route step."));
            }
            else if (workOrder.RoutingScope == PomWorkOrderRoutingScope.SerialRoute)
            {
                if (string.IsNullOrWhiteSpace(workOrder.RoutingId))
                    return Result.Failure<Lot>(Error.Conflict(
                        "SERIAL_ROUTE_CONFIGURATION_INVALID: the work order has no routing ID."));

                var productRouting = await _master.GetProductRoutingAsync(workOrder.RoutingId, ct);
                if (productRouting is null)
                    return Result.Failure<Lot>(Error.NotFound(
                        "Routing", $"Routing '{workOrder.RoutingId}' was not found."));
                if (!string.Equals(productRouting.ProductId, workOrder.ProductId, StringComparison.OrdinalIgnoreCase))
                    return Result.Failure<Lot>(Error.Conflict(
                        "SERIAL_ROUTE_PRODUCT_MISMATCH: routing product does not match the work order."));
                if (productRouting.Steps.Count == 0 ||
                    productRouting.Steps.Any(step => string.IsNullOrWhiteSpace(step.ProcessId)))
                    return Result.Failure<Lot>(Error.Conflict(
                        "SERIAL_ROUTE_PROCESS_MAPPING_REQUIRED: every routing step must map to a process."));

                var masterRouteSteps = productRouting.Steps
                    .OrderBy(step => step.StepNo)
                    .Select(step => step.ProcessId.Trim())
                    .ToList();
                if (normalizedRouteSteps.Count > 0 &&
                    !normalizedRouteSteps.SequenceEqual(masterRouteSteps, StringComparer.OrdinalIgnoreCase))
                    return Result.Failure<Lot>(Error.Validation(nameof(routeSteps),
                        "Serial-route LOT steps must exactly match the selected product routing."));

                // The MDM routing is authoritative. An empty caller list is populated automatically;
                // an explicit list is accepted only after the exact-order check above.
                normalizedRouteSteps = masterRouteSteps;
            }
            var siblings = await _lots.GetByWorkOrderAsync(workOrder.Id, ct);
            var allocatedQty = 0m;
            foreach (var sibling in siblings)
            {
                if (!ProductionQuantityBoundary.TryAdd(allocatedQty, sibling.Qty, out allocatedQty))
                    return Result.Failure<Lot>(Error.Validation(
                        nameof(qty), "Existing lot quantity total does not fit DECIMAL(18,4)."));
            }
            if (!ProductionQuantityBoundary.TryAdd(allocatedQty, qty, out var requestedTotalQty) ||
                requestedTotalQty > workOrder.PlanQty)
                return Result.Failure<Lot>(Error.Validation(nameof(qty), "Lot quantities cannot exceed the work-order plan quantity."));
        }

        // Transport callers can still deserialize null into non-nullable parameters. Normalize at
        // the application boundary and let Lot.Create return the canonical validation error.
        var result = Lot.Create(
            lotId ?? string.Empty,
            plantId ?? string.Empty,
            workOrderId,
            productId ?? string.Empty,
            qty,
            normalizedRouteSteps,
            user);
        if (result.IsFailure) return result;

        await _lots.AddAsync(result.Value, ct);
        return result;
    }

    // ── TrackIn (설계 19.4.4) ────────────────────────────────────────────────

    public async Task<Result<Lot>> TrackInAsync(TrackInCommand command, CancellationToken ct = default)
    {
        if (!IsSupportedChannel(command.ClientChannel))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var auditInput = ValidateRoutingAuditInput(null, command.DeviceId);
        if (auditInput.IsFailure) return Result.Failure<Lot>(auditInput.Error);
        if (string.IsNullOrWhiteSpace(command.User))
            return Result.Failure<Lot>(Error.Validation(nameof(command.User), "User is required."));
        if (command.User.Trim().Length > PomStorageBoundary.ActorLength)
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.User), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var user = command.User.Trim();
        var clientChannel = NormalizeChannel(command.ClientChannel);

        // 1. Lot 존재 및 Plant 일치
        var lot = await _lots.GetByIdAsync(command.LotId, ct);
        if (lot is null)
            return Result.Failure<Lot>(Error.NotFoundOf(nameof(Lot), command.LotId));
        if (!string.Equals(lot.PlantId, command.PlantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Lot>(Error.Validation(nameof(command.PlantId), "Lot의 Plant와 요청 Plant가 일치하지 않습니다."));
        if (lot.CurrentProcessId.Length > Lot.MaxProcessIdLength)
            return Result.Failure<Lot>(Error.Validation(
                "currentProcessId",
                $"Current process ID cannot exceed {Lot.MaxProcessIdLength} characters."));
        var requestHash = HashRequest("TrackIn", lot.Id, command.PlantId, command.EquipmentId,
            command.RecipeDefId, command.RecipeDefVersion?.ToString(CultureInfo.InvariantCulture),
            user, clientChannel, command.DeviceId);
        var transition = await PrepareTransitionAsync(
            lot, LotExecutionId.TrackIn, command.ExpectedVersion, command.IdempotencyKey, requestHash, ct);
        if (transition.IsFailure)
            return Result.Failure<Lot>(transition.Error);
        if (transition.Value.IsReplay)
            return Result.Success(lot);

        // 2~3. 상태/Hold — 설비 조회 전에 차단 (설계 검증 순서 유지)
        if (lot.IsHold)
            return Result.Failure<Lot>(Error.Conflict("Hold 상태 Lot은 TrackIn할 수 없습니다."));
        if (lot.State != LotState.Queued)
            return Result.Failure<Lot>(Error.Conflict($"Lot 상태 {lot.State}에서는 TrackIn할 수 없습니다."));

        // WorkOrder is a distinct executable aggregate. Never resolve this ID through ProductionOrder.
        PomWorkOrder? workOrder = null;
        if (lot.WorkOrderId is not null)
        {
            workOrder = await _workOrders.GetByIdAsync(lot.WorkOrderId, ct);
            if (workOrder is null)
                return Result.Failure<Lot>(Error.NotFoundOf(nameof(PomWorkOrder), lot.WorkOrderId));
            if (!string.Equals(workOrder.PlantId, lot.PlantId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(workOrder.ProductId, lot.ProductId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Conflict("Lot and work-order plant/product do not match."));
            if (workOrder.Status is not (PomWorkOrderStatus.Released or PomWorkOrderStatus.Started))
                return Result.Failure<Lot>(Error.Conflict("Only a released or started work order can execute a lot."));
            if (workOrder.IsHold)
                return Result.Failure<Lot>(Error.Conflict("A held work order cannot execute a lot."));
            if (!workOrder.IsSerialRouting && !string.IsNullOrWhiteSpace(workOrder.EquipmentId) &&
                !string.Equals(workOrder.EquipmentId, command.EquipmentId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Conflict("The requested equipment does not match the work order."));
            if (!string.IsNullOrWhiteSpace(workOrder.ProcessId) &&
                !string.Equals(workOrder.ProcessId, lot.CurrentProcessId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Conflict("The lot process does not match the work order."));

            // LOT TrackIn can auto-start a Released W/O, so it must use the same predecessor guard
            // as the explicit W/O Start endpoint. Otherwise TrackIn would bypass strict sequencing.
            var predecessor = await WorkOrderRoutingPredecessorGuard.ValidateAsync(
                _workOrders, workOrder, ct);
            if (predecessor.IsFailure)
                return Result.Failure<Lot>(predecessor.Error);
        }

        // 4. 설비 존재/Plant 일치/사용 가능
        var equipment = await ValidateEquipmentAsync(command.EquipmentId ?? string.Empty, command.PlantId!, ct);
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
        var trackIn = lot.TrackIn(
            equipment.Value.EquipmentId,
            command.RecipeDefId,
            command.RecipeDefVersion,
            user,
            serverTime);
        if (trackIn.IsFailure)
            return Result.Failure<Lot>(trackIn.Error);

        var history = LotHistory.Of(lot, LotExecutionId.TrackIn, user, lot.Qty, 0) with
        {
            IdempotencyKey = transition.Value.IdempotencyKey
        };
        PomWorkOrder? transitionedWorkOrder = null;
        PomWorkOrderExecution? workOrderExecution = null;
        if (workOrder is { Status: PomWorkOrderStatus.Released })
        {
            var from = workOrder.Status;
            var expectedWorkOrderVersion = workOrder.VersionNo;
            if (workOrder.Start(serverTime, user).IsSuccess)
            {
                transitionedWorkOrder = workOrder;
                workOrderExecution = new PomWorkOrderExecution(
                    Guid.NewGuid().ToString("N"), workOrder.Id,
                    $"LOT:{lot.Id}:TRACK_IN", PomWorkOrderAction.Start,
                    from, workOrder.Status, null, null, user, command.EquipmentId,
                    clientChannel, Trimmed(command.DeviceId), serverTime, $"Auto start by lot {lot.Id}",
                    ExpectedVersion: expectedWorkOrderVersion,
                    ResultVersion: expectedWorkOrderVersion + 1);
            }
        }
        var routingAudit = new RoutingTransitionAudit(
            lot.CurrentStepIndex, lot.CurrentStepIndex,
            lot.CurrentProcessId, lot.CurrentProcessId,
            lot.ControlMode, null, clientChannel, Trimmed(command.DeviceId), "TrackIn");
        var persisted = await PersistTransitionAsync(
            lot, transition.Value, [history], transitionedWorkOrder, workOrderExecution, ct,
            routingAudit: routingAudit);
        if (!persisted)
            return Result.Failure<Lot>(Error.Conflict("Lot was changed by another request."));
        return Result.Success(lot);
    }

    // ── TrackOut (설계 19.4.5) ───────────────────────────────────────────────

    public async Task<Result<Lot>> TrackOutAsync(TrackOutCommand command, CancellationToken ct = default)
    {
        if (!IsSupportedChannel(command.ClientChannel))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var auditInput = ValidateRoutingAuditInput(null, command.DeviceId);
        if (auditInput.IsFailure) return Result.Failure<Lot>(auditInput.Error);
        if (string.IsNullOrWhiteSpace(command.User))
            return Result.Failure<Lot>(Error.Validation(nameof(command.User), "User is required."));
        if (command.User.Trim().Length > PomStorageBoundary.ActorLength)
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.User), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var user = command.User.Trim();
        var clientChannel = NormalizeChannel(command.ClientChannel);

        // 1. Lot 존재 및 Plant 일치
        var lot = await _lots.GetByIdAsync(command.LotId, ct);
        if (lot is null)
            return Result.Failure<Lot>(Error.NotFoundOf(nameof(Lot), command.LotId));
        if (!string.Equals(lot.PlantId, command.PlantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Lot>(Error.Validation(nameof(command.PlantId), "Lot의 Plant와 요청 Plant가 일치하지 않습니다."));

        // 6. 불량 코드 유효성 — 상태 변경 전에 전부 검증
        if (lot.CurrentProcessId.Length > Lot.MaxProcessIdLength)
            return Result.Failure<Lot>(Error.Validation(
                "currentProcessId",
                $"Current process ID cannot exceed {Lot.MaxProcessIdLength} characters."));
        if (!ProductionQuantityBoundary.Fits(command.Qty))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.Qty), "Production quantity must fit DECIMAL(18,4)."));

        var defects = command.Defects ?? [];
        if (defects.Any(static d => d is null))
            return Result.Failure<Lot>(Error.Validation(
                "defects", "Defect entries cannot contain null items."));
        if (defects.Any(d => d.DefectQty <= 0))
            return Result.Failure<Lot>(Error.Validation("defects", "불량코드별 수량은 0보다 커야 합니다."));
        if (defects.Any(d => !ProductionQuantityBoundary.Fits(d.DefectQty)))
            return Result.Failure<Lot>(Error.Validation(
                "defects", "불량코드별 수량은 DECIMAL(18,4) 범위여야 합니다."));
        if (defects.Select(d => d.DefectCode?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count() != defects.Count)
            return Result.Failure<Lot>(Error.Validation("defects", "불량 코드가 중복되었습니다."));
        if (defects.Any(d => string.IsNullOrWhiteSpace(d.DefectCode)))
            return Result.Failure<Lot>(Error.Validation("defects", "불량 코드는 필수입니다."));
        if (defects.Any(d => d.DefectCode.Trim().Length > 50))
            return Result.Failure<Lot>(Error.Validation(
                "defects", "불량 코드는 50자를 초과할 수 없습니다."));
        var totalDefectQty = 0m;
        foreach (var defect in defects)
        {
            if (!ProductionQuantityBoundary.TryAdd(totalDefectQty, defect.DefectQty, out totalDefectQty))
                return Result.Failure<Lot>(Error.Validation(
                    "defects", "Total defect quantity must fit DECIMAL(18,4)."));
        }
        var hashFields = new List<object?>
        {
            "TrackOut", lot.Id, command.PlantId, command.EquipmentId, command.Qty,
        };
        foreach (var defect in defects.OrderBy(d => d.DefectCode, StringComparer.OrdinalIgnoreCase))
        {
            hashFields.Add(defect.DefectCode?.Trim());
            hashFields.Add(defect.DefectQty);
        }
        hashFields.Add(command.CarrierId);
        hashFields.Add(user);
        hashFields.Add(clientChannel);
        hashFields.Add(command.DeviceId);
        var requestHash = HashRequest(hashFields.ToArray());
        var transition = await PrepareTransitionAsync(
            lot, LotExecutionId.TrackOut, command.ExpectedVersion, command.IdempotencyKey, requestHash, ct);
        if (transition.IsFailure)
            return Result.Failure<Lot>(transition.Error);
        if (transition.Value.IsReplay)
            return Result.Success(lot);

        // Master data is mutable. Resolve an already-committed exact retry before consulting it,
        // otherwise disabling a defect code after TrackOut would break idempotent replay.
        foreach (var defect in defects)
        {
            if (!await _master.IsValidDefectCodeAsync(defect.DefectCode.Trim(), ct))
                return Result.Failure<Lot>(Error.Validation(
                    "defects", $"유효하지 않은 불량 코드입니다: '{defect.DefectCode}'"));
        }

        // 이력에는 반납 전 설비/Recipe/공정을 기록한다 — TrackOut이 점유를 비우기 때문
        var equipmentBefore = lot.EquipmentId;
        var stepBefore = lot.CurrentStepIndex;
        var recipeBefore = lot.RecipeDefId;
        var recipeVersionBefore = lot.RecipeDefVersion;
        var processBefore = lot.CurrentProcessId;
        var trackInTimeBefore = lot.TrackInTime;
        var wasInRework = lot.IsInRework;
        var returnStepBefore = lot.ReturnStepIndex;

        // Every process with active QMS specifications is a hard quality boundary, including
        // intermediate and rework steps. Evaluate before mutation so automatic Return cannot
        // bypass pending or failed inspection evidence.
        var canTrackOut = !lot.IsHold && lot.State == LotState.Processing &&
            string.Equals(lot.EquipmentId, command.EquipmentId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            command.Qty > 0 && command.Qty <= lot.Qty &&
            lot.DefectQty + totalDefectQty <= command.Qty;
        if (canTrackOut)
        {
            var quality = await _productionQuality.EvaluateAsync(
                lot.Id, processBefore, lot.WorkOrderId, ct);
            if (quality is null)
                return Result.Failure<Lot>(Error.Conflict(
                    "Production quality gate is unavailable; TrackOut is blocked."));
            if (!quality.AllowsCompletion)
            {
                var blockingSpec = string.IsNullOrWhiteSpace(quality.BlockingSpecId)
                    ? string.Empty
                    : $" Blocking specification: {quality.BlockingSpecId}.";
                return Result.Failure<Lot>(Error.Conflict(
                    $"Production quality gate is {quality.Status}; TrackOut is blocked.{blockingSpec}"));
            }
        }

        // 2~5, 7. 상태/설비 일치/Hold/수량 범위 + 서버 시각 — 도메인에서 검증
        var serverTime = DateTime.UtcNow;
        var trackOut = lot.TrackOut(
            command.EquipmentId ?? string.Empty,
            command.Qty,
            totalDefectQty,
            command.CarrierId,
            user,
            serverTime);
        if (trackOut.IsFailure)
            return Result.Failure<Lot>(trackOut.Error);

        // Generate the immutable execution identity before derived histories so an automatic Return
        // can correlate to its TrackOut execution without reusing the request idempotency key.
        var executionId = Guid.NewGuid().ToString("N");
        var histories = new List<LotHistory>
        {
            new(
            0, lot.PlantId, lot.Id, equipmentBefore, processBefore,
            recipeBefore, recipeVersionBefore, trackInTimeBefore, lot.TrackOutTime,
            LotExecutionId.TrackOut, user, lot.Qty, totalDefectQty,
            lot.State.ToString(), lot.ProcessState.ToString(), serverTime,
            IdempotencyKey: transition.Value.IdempotencyKey)
        };

        if (lot.State == LotState.Completed)
            histories.Add(LotHistory.Of(lot, LotExecutionId.Finish, user, lot.Qty, lot.DefectQty));
        else if (wasInRework && returnStepBefore.HasValue)
            histories.Add(LotHistory.Of(
                lot, LotExecutionId.Return, user, lot.Qty, lot.DefectQty) with
            {
                Reason = PomStorageBoundary.HistorySummary(
                    $"Automatic return after rework TrackOut: {processBefore} -> {lot.CurrentProcessId}"),
                IdempotencyKey = executionId
            });

        var (workOrder, workOrderExecution) = lot.State == LotState.Completed
            ? await PrepareFinishWorkOrderAsync(
                lot, user, serverTime, clientChannel, command.DeviceId, ct)
            : (null, null);
        var defectExecutions = defects.Select(defect => new LotDefectExecution(
            executionId,
            lot.Id,
            lot.PlantId,
            processBefore,
            defect.DefectCode.Trim(),
            defect.DefectQty,
            user,
            clientChannel,
            Trimmed(command.DeviceId),
            serverTime)).ToList();
        var routingAudit = new RoutingTransitionAudit(
            stepBefore, lot.CurrentStepIndex, processBefore, lot.CurrentProcessId,
            lot.ControlMode, null, clientChannel, Trimmed(command.DeviceId),
            wasInRework ? "TrackOut and automatic rework Return" : "TrackOut");
        var persisted = await PersistTransitionAsync(
            lot, transition.Value, histories, workOrder, workOrderExecution, ct,
            routingAudit: routingAudit, executionId: executionId,
            defectExecutions: defectExecutions);
        if (!persisted)
            return Result.Failure<Lot>(Error.Conflict("Lot was changed by another request."));
        return Result.Success(lot);
    }

    /// <summary>
    /// 같은 작업지시의 모든 Lot이 완료되면 라우팅 LOT 전이와 같은 트랜잭션에서 W/O를 자동 완료한다.
    /// 라우팅 결합 작업지시는 직접 완료가 차단되므로, 자동 완료 조건이 아니면 Started 상태를 유지한다.
    /// </summary>
    private async Task<(PomWorkOrder? WorkOrder, PomWorkOrderExecution? Execution)> PrepareFinishWorkOrderAsync(
        Lot lot, string user, DateTime serverTime, string clientChannel,
        string? deviceId, CancellationToken ct)
    {
        if (lot.WorkOrderId is null) return (null, null);

        var siblings = await _lots.GetByWorkOrderAsync(lot.WorkOrderId, ct);
        var effectiveSiblings = siblings
            .Where(l => !string.Equals(l.Id, lot.Id, StringComparison.OrdinalIgnoreCase))
            .Append(lot).ToList();
        if (effectiveSiblings.Any(l => l.State is not (LotState.Completed or LotState.Consumed)))
            return (null, null);

        var workOrder = await _workOrders.GetByIdAsync(lot.WorkOrderId, ct);
        if (workOrder is null || workOrder.Status != PomWorkOrderStatus.Started) return (null, null);

        var completed = effectiveSiblings.Where(l => l.State == LotState.Completed).ToList();
        var defectQty = completed.Sum(l => l.DefectQty);
        var goodQty = completed.Sum(l => Math.Max(0, l.Qty - l.DefectQty));
        var from = workOrder.Status;
        var expectedWorkOrderVersion = workOrder.VersionNo;
        if (workOrder.Complete(goodQty, defectQty, serverTime, user).IsFailure)
            return (null, null);
        return (workOrder, new PomWorkOrderExecution(
                Guid.NewGuid().ToString("N"), workOrder.Id, $"WO:{workOrder.Id}:AUTO_COMPLETE", PomWorkOrderAction.Complete,
                from, workOrder.Status, goodQty, defectQty, user, workOrder.EquipmentId,
                clientChannel, Trimmed(deviceId), serverTime, "Auto complete after all lots finished",
                ExpectedVersion: expectedWorkOrderVersion,
                ResultVersion: expectedWorkOrderVersion + 1));
    }

    // ── Mixing TrackIn/TrackOut (설계 19.4.7) ────────────────────────────────

    public async Task<Result<Lot>> MixingTrackInOutAsync(MixingTrackCommand command, CancellationToken ct = default)
    {
        var plantId = command.PlantId?.Trim() ?? string.Empty;
        if (plantId.Length == 0)
            return Result.Failure<Lot>(Error.Validation(nameof(command.PlantId), "Plant ID is required."));
        if (!PomStorageBoundary.FitsRequired(command.User, PomStorageBoundary.ActorLength))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.User), $"User is required and cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (command.Inputs is null || command.Inputs.Count == 0)
            return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), "투입 Lot 목록이 비어 있습니다."));
        if (command.Inputs.Any(static i => i is null))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.Inputs), "Mixing inputs cannot contain null items."));
        if (command.Inputs.Any(i => i.InQty <= 0))
            return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), "투입 수량은 0보다 커야 합니다."));
        if (command.Inputs.Any(i => !ProductionQuantityBoundary.Fits(i.InQty)))
            return Result.Failure<Lot>(Error.Validation(
                nameof(command.Inputs), "Mixing input quantity must fit DECIMAL(18,4)."));
        if (command.Inputs.Select(i => i.LotId?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != command.Inputs.Count)
            return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), "투입 Lot이 중복되었습니다."));

        // 입력 Lot 전수 검증 — 상태 변경 전에 모두 통과해야 한다 (UnitOfWork 부재 적응)
        var totalQty = 0m;
        foreach (var input in command.Inputs)
        {
            if (!ProductionQuantityBoundary.TryAdd(totalQty, input.InQty, out totalQty))
                return Result.Failure<Lot>(Error.Validation(
                    nameof(command.Inputs), "Total mixing quantity must fit DECIMAL(18,4)."));
        }

        var inputs = new List<(Lot Lot, decimal InQty)>();
        foreach (var input in command.Inputs)
        {
            var lot = await _lots.GetByIdAsync(input.LotId?.Trim() ?? string.Empty, ct);
            if (lot is null)
                return Result.Failure<Lot>(Error.NotFoundOf(nameof(Lot), input.LotId ?? string.Empty));
            if (!string.Equals(lot.PlantId, plantId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), $"투입 Lot '{lot.Id}'의 Plant가 일치하지 않습니다."));
            if (lot.IsHold)
                return Result.Failure<Lot>(Error.Conflict($"투입 Lot '{lot.Id}'이(가) Hold 상태입니다."));
            if (!LotStateMachine.CanTransition(lot.State, LotState.Consumed))
                return Result.Failure<Lot>(Error.Conflict($"투입 Lot '{lot.Id}'의 상태 {lot.State}에서는 소비할 수 없습니다."));
            if (input.InQty != lot.Qty)
                return Result.Failure<Lot>(Error.Validation(nameof(command.Inputs), $"투입 수량이 Lot '{lot.Id}' 수량을 초과합니다."));
            inputs.Add((lot, input.InQty));
        }
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

        // DATA-3 원자화 — 도메인 전이를 전부 in-memory로 수행하며 이력/관계 스냅샷만 수집한다.
        // 어떤 전이든 실패하면 아무것도 쓰지 않고 반환하고, 성공 시에만 마지막에 단일 트랜잭션으로 영속한다.
        var histories = new List<LotHistory>();
        var relations = new List<LotMixingRelation>();

        // 입력 Lot 소비 + 관계 스냅샷
        foreach (var (lot, inQty) in inputs)
        {
            var consumed = lot.Consume(command.User);
            if (consumed.IsFailure)
                return Result.Failure<Lot>(consumed.Error);
            histories.Add(LotHistory.Of(lot, LotExecutionId.Consume, command.User, inQty, 0));
            relations.Add(new LotMixingRelation(
                plantId, output.Id, lot.Id, inQty,
                totalQty == 0 ? null : Math.Round(inQty / totalQty, 4), serverTime, command.User));
        }

        // 출력 Lot TrackIn -> TrackOut 연속 수행(in-memory) — 이력은 각 전이 시점 상태로 캡처.
        var trackIn = output.TrackIn(command.EquipmentId, null, null, command.User, serverTime);
        if (trackIn.IsFailure)
            return Result.Failure<Lot>(trackIn.Error);
        histories.Add(LotHistory.Of(output, LotExecutionId.TrackIn, command.User, output.Qty, 0));

        var equipmentBefore = output.EquipmentId;
        var processBefore = output.CurrentProcessId;
        var trackInTimeBefore = output.TrackInTime;
        var trackOut = output.TrackOut(command.EquipmentId, output.Qty, 0, null, command.User, DateTime.UtcNow);
        if (trackOut.IsFailure)
            return Result.Failure<Lot>(trackOut.Error);
        histories.Add(new LotHistory(
            0, output.PlantId, output.Id, equipmentBefore, processBefore,
            null, null, trackInTimeBefore, output.TrackOutTime,
            LotExecutionId.TrackOut, command.User, output.Qty, 0,
            output.State.ToString(), output.ProcessState.ToString(), DateTime.UtcNow));
        if (output.State == LotState.Completed)
            histories.Add(LotHistory.Of(output, LotExecutionId.Finish, command.User, output.Qty, output.DefectQty));

        // 단일 트랜잭션 영속 — 실패 시 전체 롤백(부분 커밋 불가).
        await _lots.MixingPersistAsync(new MixingPersistPlan(
            inputs.Select(i => i.Lot).ToList(), output, isNewOutput, histories, relations), ct);

        return Result.Success(output);
    }

    // ── Hold / Release ───────────────────────────────────────────────────────

    public async Task<Result> HoldAsync(
        string lotId, string user, int expectedVersion, string idempotencyKey,
        string? reason = null, string clientChannel = "MES", string? deviceId = null,
        CancellationToken ct = default)
    {
        if (!IsSupportedChannel(clientChannel))
            return Result.Failure(Error.Validation(
                nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        var auditInput = ValidateRoutingAuditInput(reason, deviceId);
        if (auditInput.IsFailure) return auditInput;
        if (string.IsNullOrWhiteSpace(user))
            return Result.Failure(Error.Validation(nameof(user), "User is required."));
        if (user.Trim().Length > PomStorageBoundary.ActorLength)
            return Result.Failure(Error.Validation(
                nameof(user), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var normalizedUser = user.Trim();
        var normalizedChannel = NormalizeChannel(clientChannel);
        var lot = await _lots.GetByIdAsync(lotId, ct);
        if (lot is null)
            return Result.Failure(Error.NotFoundOf(nameof(Lot), lotId));
        var requestHash = HashRequest(
            LotExecutionId.Hold, lot.Id, reason, normalizedUser, normalizedChannel, deviceId);
        var transition = await PrepareTransitionAsync(
            lot, LotExecutionId.Hold, expectedVersion, idempotencyKey, requestHash, ct);
        if (transition.IsFailure) return Result.Failure(transition.Error);
        if (transition.Value.IsReplay) return Result.Success();
        var held = lot.Hold(normalizedUser);
        if (held.IsFailure) return held;

        var history = LotHistory.Of(lot, LotExecutionId.Hold, normalizedUser, lot.Qty, lot.DefectQty) with
        {
            Reason = PomStorageBoundary.HistorySummary(
                string.Empty, string.IsNullOrWhiteSpace(reason) ? "No reason supplied" : reason),
            IdempotencyKey = transition.Value.IdempotencyKey
        };
        var audit = new RoutingTransitionAudit(
            lot.CurrentStepIndex, lot.CurrentStepIndex, lot.CurrentProcessId, lot.CurrentProcessId,
            lot.ControlMode, null, normalizedChannel, Trimmed(deviceId), history.Reason);
        if (!await PersistTransitionAsync(
                lot, transition.Value, [history], null, null, ct, routingAudit: audit))
            return Result.Failure(Error.Conflict("Lot was changed by another request."));
        return Result.Success();
    }

    public async Task<Result> ReleaseHoldAsync(
        string lotId, string user, int expectedVersion, string idempotencyKey,
        string? reason = null, string clientChannel = "MES", string? deviceId = null,
        CancellationToken ct = default)
    {
        if (!IsSupportedChannel(clientChannel))
            return Result.Failure(Error.Validation(
                nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        var auditInput = ValidateRoutingAuditInput(reason, deviceId);
        if (auditInput.IsFailure) return auditInput;
        if (string.IsNullOrWhiteSpace(user))
            return Result.Failure(Error.Validation(nameof(user), "User is required."));
        if (user.Trim().Length > PomStorageBoundary.ActorLength)
            return Result.Failure(Error.Validation(
                nameof(user), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        var normalizedUser = user.Trim();
        var normalizedChannel = NormalizeChannel(clientChannel);
        var lot = await _lots.GetByIdAsync(lotId, ct);
        if (lot is null)
            return Result.Failure(Error.NotFoundOf(nameof(Lot), lotId));
        var requestHash = HashRequest(
            LotExecutionId.ReleaseHold, lot.Id, reason, normalizedUser, normalizedChannel, deviceId);
        var transition = await PrepareTransitionAsync(
            lot, LotExecutionId.ReleaseHold, expectedVersion, idempotencyKey, requestHash, ct);
        if (transition.IsFailure) return Result.Failure(transition.Error);
        if (transition.Value.IsReplay) return Result.Success();
        var released = lot.ReleaseHold(normalizedUser);
        if (released.IsFailure) return released;

        var history = LotHistory.Of(lot, LotExecutionId.ReleaseHold, normalizedUser, lot.Qty, lot.DefectQty) with
        {
            Reason = PomStorageBoundary.HistorySummary(
                string.Empty, string.IsNullOrWhiteSpace(reason) ? "No reason supplied" : reason),
            IdempotencyKey = transition.Value.IdempotencyKey
        };
        var audit = new RoutingTransitionAudit(
            lot.CurrentStepIndex, lot.CurrentStepIndex, lot.CurrentProcessId, lot.CurrentProcessId,
            lot.ControlMode, null, normalizedChannel, Trimmed(deviceId), history.Reason);
        if (!await PersistTransitionAsync(
                lot, transition.Value, [history], null, null, ct, routingAudit: audit))
            return Result.Failure(Error.Conflict("Lot was changed by another request."));
        return Result.Success();
    }

    // ── 내부 헬퍼 ─────────────────────────────────────────────────────────────

    private sealed record TransitionRequest(
        int ExpectedVersion, string IdempotencyKey, string RequestHash, string Action, bool IsReplay);

    private async Task<Result<TransitionRequest>> PrepareTransitionAsync(
        Lot lot, string action, int requestedVersion, string? requestedKey, string requestHash,
        CancellationToken ct)
    {
        if (requestedVersion < 1)
            return Result.Failure<TransitionRequest>(
                Error.Validation(nameof(requestedVersion), "Expected version must be at least 1."));

        if (string.IsNullOrWhiteSpace(requestedKey))
            return Result.Failure<TransitionRequest>(
                Error.Validation(nameof(requestedKey), "Idempotency key is required."));

        var idempotencyKey = requestedKey.Trim();
        if (idempotencyKey.Length > 100)
            return Result.Failure<TransitionRequest>(
                Error.Validation(nameof(requestedKey), "Idempotency key cannot exceed 100 characters."));

        var previous = await _atomicLots.GetExecutionAsync(idempotencyKey, ct);
        if (previous is not null)
        {
            var exactReplay = string.Equals(previous.LotId, lot.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.Action, action, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.RequestHash, requestHash, StringComparison.Ordinal)
                && previous.ExpectedVersion == requestedVersion;
            if (!exactReplay)
                return Result.Failure<TransitionRequest>(
                    Error.Conflict("The idempotency key was already used for a different lot operation."));
            return Result.Success(new TransitionRequest(
                requestedVersion, idempotencyKey, requestHash, action, IsReplay: true));
        }

        if (lot.VersionNo != requestedVersion)
            return Result.Failure<TransitionRequest>(
                Error.Conflict($"Lot version conflict. Expected {requestedVersion}, current {lot.VersionNo}."));

        return Result.Success(new TransitionRequest(
            requestedVersion, idempotencyKey, requestHash, action, IsReplay: false));
    }

    private async Task<bool> PersistTransitionAsync(
        Lot lot,
        TransitionRequest transition,
        IReadOnlyList<LotHistory> histories,
        PomWorkOrder? workOrder,
        PomWorkOrderExecution? workOrderExecution,
        CancellationToken ct,
        RouteExceptionRequest? routeException = null,
        RoutingTransitionAudit? routingAudit = null,
        string? executionId = null,
        IReadOnlyList<LotDefectExecution>? defectExecutions = null)
    {
        var persistResult = await _atomicLots.PersistTransitionAsync(new LotTransitionPersistPlan(
            lot, transition.ExpectedVersion, transition.Action, transition.IdempotencyKey,
            transition.RequestHash, histories, workOrder, workOrderExecution,
            routeException, routingAudit, executionId, defectExecutions), ct);
        if (persistResult == LotTransitionPersistResult.Persisted) return true;
        if (persistResult != LotTransitionPersistResult.Conflict)
            throw new InvalidOperationException($"Unknown lot transition persist result '{persistResult}'.");

        var concurrent = await _atomicLots.GetExecutionAsync(transition.IdempotencyKey, ct);
        return concurrent is not null
            && string.Equals(concurrent.LotId, lot.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(concurrent.Action, transition.Action, StringComparison.OrdinalIgnoreCase)
            && string.Equals(concurrent.RequestHash, transition.RequestHash, StringComparison.Ordinal)
            && concurrent.ExpectedVersion == transition.ExpectedVersion;
    }

    /// <summary>예외 요청 승인·반려의 공통 조회, 서버시각 상태 전이와 조건부 저장을 수행한다.</summary>
    private async Task<Result<RouteExceptionRequest>> ReviewRouteExceptionAsync(
        ReviewRouteExceptionCommand command, bool approve, CancellationToken ct)
    {
        if (_lots is not IRouteExceptionRepository repository)
            return Result.Failure<RouteExceptionRequest>(Error.Conflict(
                "The configured lot repository does not support route exceptions."));
        if (!IsSupportedChannel(command.ClientChannel))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (Trimmed(command.DeviceId)?.Length > RouteExceptionRequest.MaxDeviceIdLength)
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.DeviceId),
                $"Device ID cannot exceed {RouteExceptionRequest.MaxDeviceIdLength} characters."));
        if (!PomStorageBoundary.FitsRequired(command.Reviewer, PomStorageBoundary.ActorLength))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.Reviewer),
                $"Reviewer is required and cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (!PomStorageBoundary.FitsOptional(command.Reason, PomStorageBoundary.ReasonLength))
            return Result.Failure<RouteExceptionRequest>(Error.Validation(
                nameof(command.Reason),
                $"Review reason cannot exceed {PomStorageBoundary.ReasonLength} characters."));

        var request = await repository.GetRouteExceptionAsync(command.ExceptionId?.Trim() ?? string.Empty, ct);
        if (request is null)
            return Result.Failure<RouteExceptionRequest>(
                Error.NotFoundOf(nameof(RouteExceptionRequest), command.ExceptionId ?? string.Empty));

        request = await PersistExpirationForWriteAsync(
            repository, request, DateTime.UtcNow, ct);
        if (request.Status == RouteExceptionStatus.Expired)
            return Result.Failure<RouteExceptionRequest>(Error.Conflict(
                "The route exception has expired."));

        // 동일 검토자의 정확한 네트워크 재시도는 두 번째 UPDATE 없이 현재 승인 원장을 반환한다.
        if (IsExactReviewReplay(request, command, approve))
            return Result.Success(request);

        var result = approve
            ? request.Approve(
                command.Reviewer ?? string.Empty, command.Reason, DateTime.UtcNow,
                command.ClientChannel, command.DeviceId)
            : request.Reject(
                command.Reviewer ?? string.Empty, command.Reason ?? string.Empty, DateTime.UtcNow,
                command.ClientChannel, command.DeviceId);

        // 만료 판정도 원장에 남긴다. 그 외 검증 실패는 상태를 바꾸지 않았으므로 저장하지 않는다.
        if (result.IsFailure)
        {
            if (request.Status == RouteExceptionStatus.Expired)
                await repository.UpdateRouteExceptionAsync(request, RouteExceptionStatus.Requested, ct);
            return Result.Failure<RouteExceptionRequest>(result.Error);
        }

        var updated = await repository.UpdateRouteExceptionAsync(
            request, RouteExceptionStatus.Requested, ct);
        if (updated)
            return Result.Success(request);

        var concurrent = await repository.GetRouteExceptionAsync(request.Id, ct);
        return concurrent is not null && IsExactReviewReplay(concurrent, command, approve)
            ? Result.Success(concurrent)
            : Result.Failure<RouteExceptionRequest>(Error.Conflict(
                "The route exception was reviewed concurrently."));
    }

    /// <summary>
    /// Read endpoints expose effective server-time expiration without mutating the approval ledger.
    /// The actual Expired transition is persisted only by review/apply write paths.
    /// </summary>
    private static RouteExceptionRequest ProjectExpirationForRead(
        RouteExceptionRequest request,
        DateTime now)
    {
        if (request.IsExpired(now))
            request.MarkExpired(now);
        return request;
    }

    /// <summary>
    /// Persists Expired on a write path. A failed conditional update reloads the concurrent winner.
    /// </summary>
    private static async Task<RouteExceptionRequest> PersistExpirationForWriteAsync(
        IRouteExceptionRepository repository,
        RouteExceptionRequest request,
        DateTime now,
        CancellationToken ct)
    {
        if (!request.IsExpired(now))
            return request;

        var expected = request.Status;
        request.MarkExpired(now);
        if (await repository.UpdateRouteExceptionAsync(request, expected, ct))
            return request;

        return await repository.GetRouteExceptionAsync(request.Id, ct) ?? request;
    }

    private static bool IsExactReviewReplay(
        RouteExceptionRequest request,
        ReviewRouteExceptionCommand command,
        bool approve) =>
        request.Status == (approve ? RouteExceptionStatus.Approved : RouteExceptionStatus.Rejected) &&
        string.Equals(request.ReviewedBy, command.Reviewer?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(request.ReviewReason, Trimmed(command.Reason), StringComparison.Ordinal) &&
        string.Equals(
            request.ReviewClientChannel, NormalizeChannel(command.ClientChannel),
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            request.ReviewDeviceId, Trimmed(command.DeviceId),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Enforces quality as a hard invariant for every process removed by a Bypass. This applies in
    /// Flexible and NoControl alike; routing flexibility never means skipping required evidence.
    /// </summary>
    private async Task<Result> ValidateBypassQualityGatesAsync(
        Lot lot,
        int targetStepIndex,
        CancellationToken ct)
    {
        for (var step = lot.CurrentStepIndex; step < targetStepIndex; step++)
        {
            var processId = lot.RouteSteps[step];
            var quality = await _productionQuality.EvaluateAsync(
                lot.Id, processId, lot.WorkOrderId, ct);
            if (quality is null)
                return Result.Failure(Error.Conflict(
                    $"ROUTE_BYPASS_QUALITY_UNAVAILABLE: process '{processId}' quality gate is unavailable."));
            if (quality.AllowsCompletion)
                continue;

            var blockingSpec = string.IsNullOrWhiteSpace(quality.BlockingSpecId)
                ? string.Empty
                : $" Blocking specification: {quality.BlockingSpecId}.";
            return Result.Failure(Error.Conflict(
                $"ROUTE_BYPASS_QUALITY_BLOCKED: process '{processId}' quality gate is " +
                $"{quality.Status}; Bypass is blocked.{blockingSpec}"));
        }

        return Result.Success();
    }

    /// <summary>LOT 존재와 Plant 경계를 모든 라우팅 유스케이스에 동일하게 적용한다.</summary>
    private async Task<Result<Lot>> LoadLotForPlantAsync(
        string lotId, string plantId, CancellationToken ct)
    {
        var normalizedLotId = lotId?.Trim() ?? string.Empty;
        var lot = await _lots.GetByIdAsync(normalizedLotId, ct);
        if (lot is null)
            return Result.Failure<Lot>(Error.NotFoundOf(nameof(Lot), normalizedLotId));
        if (!string.Equals(lot.PlantId, plantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Lot>(Error.Validation(
                nameof(plantId), "Lot plant must match the requested plant."));
        return Result.Success(lot);
    }

    private static string HashRequest(params object?[] values)
        => CanonicalRequestHash.Compute(values);

    /// <summary>
    /// Compares the immutable business identity of an exception request. Effective expiry is
    /// deliberately excluded because it is assigned/clamped by server time and changes on UI retries.
    /// </summary>
    private static bool SameRouteExceptionRequest(
        RouteExceptionRequest existing,
        RequestRouteExceptionCommand command) =>
        string.Equals(existing.LotId, command.LotId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.PlantId, command.PlantId?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        existing.DeviationType == command.DeviationType &&
        existing.ToStepIndex == command.TargetStepIndex &&
        existing.BoundLotVersion == command.ExpectedVersion &&
        string.Equals(existing.Reason, command.Reason?.Trim(), StringComparison.Ordinal) &&
        string.Equals(existing.RequestedBy, command.User?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.ClientChannel, NormalizeChannel(command.ClientChannel), StringComparison.Ordinal) &&
        string.Equals(existing.DeviceId, Trimmed(command.DeviceId), StringComparison.Ordinal);

    private async Task<Result<TrackingEquipmentInfo>> ValidateEquipmentAsync(
        string equipmentId, string plantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<TrackingEquipmentInfo>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));

        var equipment = await _master.GetEquipmentAsync(equipmentId.Trim(), ct);
        if (equipment is null)
            return Result.Failure<TrackingEquipmentInfo>(Error.NotFound("Equipment", $"Equipment '{equipmentId.Trim()}'을(를) 찾을 수 없습니다."));
        if (!string.Equals(equipment.PlantId, plantId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<TrackingEquipmentInfo>(Error.Validation(nameof(equipmentId), "설비의 Plant가 일치하지 않습니다."));
        if (!equipment.IsValid)
            return Result.Failure<TrackingEquipmentInfo>(Error.Conflict("사용 가능 상태가 아닌 설비입니다."));
        return Result.Success(equipment);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Result ValidateRoutingAuditInput(string? reason, string? deviceId)
    {
        if (Trimmed(reason)?.Length > RouteExceptionRequest.MaxReasonLength)
            return Result.Failure(Error.Validation(
                nameof(reason),
                $"Audit reason cannot exceed {RouteExceptionRequest.MaxReasonLength} characters."));
        if (Trimmed(deviceId)?.Length > RouteExceptionRequest.MaxDeviceIdLength)
            return Result.Failure(Error.Validation(
                nameof(deviceId),
                $"Device ID cannot exceed {RouteExceptionRequest.MaxDeviceIdLength} characters."));
        return Result.Success();
    }

    private static bool IsSupportedChannel(string? channel) =>
        channel?.Trim().ToUpperInvariant() is "MES" or "MOBILE" or "POP";

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string NormalizeChannel(string? channel)
    {
        var normalized = channel?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized is "MES" or "MOBILE" or "POP" ? normalized : "MES";
    }
}
