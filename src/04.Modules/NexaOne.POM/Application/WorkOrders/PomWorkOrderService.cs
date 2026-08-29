using NexaOne.Common;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.POM.Application.WorkOrders;

/// <summary>생산관리오더 아래에 새 공정 작업지시를 생성할 입력 계약이다.</summary>
public sealed record PomWorkOrderCreateCommand(
    string WorkOrderId,
    string ProductionOrderId,
    string PlantId,
    string WorkOrderName,
    string ProductId,
    decimal PlanQty,
    DateTime? PlanStartDate,
    DateTime? PlanEndDate,
    string? ProcessId,
    string? EquipmentId,
    string? OwnerId,
    string User,
    string? RoutingId = null,
    int? RoutingStepNo = null,
    string? WorkCenterId = null,
    string? AreaId = null,
    string? WorkOrderType = null,
    string? SalesOrderId = null,
    string? Description = null,
    PomWorkOrderRoutingScope? RoutingScope = null);

/// <summary>
/// 작업지시 상태 전이에 필요한 감사 사용자, 호출 채널, 멱등 키와 예상 버전을 묶는다.
/// </summary>
public sealed record PomWorkOrderOperationContext(
    string User,
    string ClientChannel,
    string IdempotencyKey,
    int ExpectedVersion,
    string? DeviceId = null,
    string? Remark = null);

/// <summary>
/// 공정 작업지시 생성과 상태 전이 유스케이스를 조정한다.
/// 생산관리오더는 부모 존재·종료 상태·제품 일치만 확인하고, 실제 공정 실행 상태는 별도 애그리거트로 관리한다.
/// </summary>
public sealed class PomWorkOrderService
{
    private readonly IPomWorkOrderRepository _workOrders;
    private readonly IProductionOrderRepository _productionOrders;
    private readonly ILotRepository _lots;
    private readonly IProductionQualityGateway _productionQuality;

    /// <summary>분리된 작업지시 및 생산관리오더 저장 계약으로 서비스를 생성한다.</summary>
    public PomWorkOrderService(
        IPomWorkOrderRepository workOrders,
        IProductionOrderRepository productionOrders,
        ILotRepository lots,
        IProductionQualityGateway productionQuality)
    {
        _workOrders = workOrders;
        _productionOrders = productionOrders;
        _lots = lots;
        _productionQuality = productionQuality;
    }

    /// <summary>
    /// 유효한 생산관리오더를 부모로 갖는 Created 상태의 공정 작업지시를 생성한다.
    /// </summary>
    public async Task<Result<PomWorkOrder>> CreateAsync(PomWorkOrderCreateCommand command, CancellationToken ct = default)
    {
        // Nullable transport values are normalized once. The domain factory remains the single
        // source of required-field validation while repository lookups never receive null IDs.
        var workOrderId = command.WorkOrderId?.Trim() ?? string.Empty;
        var productionOrderId = command.ProductionOrderId?.Trim() ?? string.Empty;
        var productId = command.ProductId?.Trim() ?? string.Empty;
        var createActor = User(command.User);
        if (createActor.Length > PomStorageBoundary.ActorLength)
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(command.User), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));

        // 생산관리오더와 작업지시는 다른 모델이므로 부모 행을 복제하지 않고 참조 무결성만 확인한다.
        var parent = await _productionOrders.GetByIdAsync(productionOrderId, ct);
        if (parent is null)
            return Result.Failure<PomWorkOrder>(Error.NotFoundOf(nameof(ProductionOrder), productionOrderId));
        if (parent.Status is ProductionOrderStatus.Completed or ProductionOrderStatus.Cancelled)
            return Result.Failure<PomWorkOrder>(Error.Conflict("A work order cannot be added to a terminal production order."));
        if (!string.Equals(parent.ProductId, productId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<PomWorkOrder>(Error.Validation(nameof(command.ProductId), "Work-order product must match the production order."));
        if (await _workOrders.GetByIdAsync(workOrderId, ct) is not null)
            return Result.Failure<PomWorkOrder>(Error.Conflict($"Work order '{command.WorkOrderId}' already exists."));

        var created = PomWorkOrder.Create(
            workOrderId, productionOrderId, command.PlantId, command.WorkOrderName,
            productId, command.PlanQty, command.PlanStartDate, command.PlanEndDate,
            command.ProcessId, command.EquipmentId, command.OwnerId, createActor,
            command.RoutingId, command.RoutingStepNo, command.WorkCenterId, command.AreaId,
            command.WorkOrderType, command.SalesOrderId, command.Description, command.RoutingScope);
        if (created.IsFailure) return created;
        await _workOrders.AddAsync(created.Value, ct);
        return created;
    }

    /// <summary>Created 작업지시를 현장 실행 가능한 Released 상태로 전환한다.</summary>
    public Task<Result<PomWorkOrder>> ReleaseAsync(string id, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(id, PomWorkOrderAction.Release, context, w => w.Release(context.User), null, null, ct);

    /// <summary>Released 작업지시의 실행을 시작한다.</summary>
    public Task<Result<PomWorkOrder>> StartAsync(string id, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(
            id, PomWorkOrderAction.Start, context,
            w => w.Start(DateTime.UtcNow, context.User), null, null, ct,
            (workOrder, token) => WorkOrderRoutingPredecessorGuard.ValidateAsync(
                _workOrders, workOrder, token));

    /// <summary>양품·불량 절대 누계를 보고하고 실행 이력을 기록한다.</summary>
    public Task<Result<PomWorkOrder>> ReportAsync(
        string id, decimal goodQty, decimal defectQty, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(id, PomWorkOrderAction.Report, context,
            w => w.ReportProduction(goodQty, defectQty, context.User), goodQty, defectQty, ct);

    /// <summary>작업지시를 보류해 추가 시작 또는 실적 보고를 차단한다.</summary>
    public Task<Result<PomWorkOrder>> HoldAsync(string id, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(id, PomWorkOrderAction.Hold, context, w => w.Hold(context.User), null, null, ct);

    /// <summary>작업지시 보류를 해제하고 기존 실행 상태를 유지한다.</summary>
    public Task<Result<PomWorkOrder>> ReleaseHoldAsync(string id, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(id, PomWorkOrderAction.ReleaseHold, context, w => w.ReleaseHold(context.User), null, null, ct);

    /// <summary>
    /// 최종 실적을 확정해 작업지시를 완료한다.
    /// 연결된 LOT이 있으면 직접 완료도 최종 공정 품질 게이트를 통과해야 한다.
    /// </summary>
    public Task<Result<PomWorkOrder>> CompleteAsync(
        string id, decimal goodQty, decimal defectQty, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(id, PomWorkOrderAction.Complete, context,
            w => w.Complete(goodQty, defectQty, DateTime.UtcNow, context.User), goodQty, defectQty, ct);

    /// <summary>아직 시작되지 않은 작업지시를 취소하고 실행 이력을 기록한다.</summary>
    public Task<Result<PomWorkOrder>> CancelAsync(string id, PomWorkOrderOperationContext context, CancellationToken ct = default)
        => MutateAsync(id, PomWorkOrderAction.Cancel, context, w => w.Cancel(context.User), null, null, ct);

    /// <summary>
    /// 공통 입력 검증, 멱등 재시도 판정, 낙관적 버전 검사와 원자적 실행 이력 저장을 거쳐 상태를 변경한다.
    /// </summary>
    private async Task<Result<PomWorkOrder>> MutateAsync(
        string id,
        PomWorkOrderAction action,
        PomWorkOrderOperationContext context,
        Func<PomWorkOrder, Result> mutation,
        decimal? goodQty,
        decimal? defectQty,
        CancellationToken ct,
        Func<PomWorkOrder, CancellationToken, Task<Result>>? precondition = null)
    {
        var normalizedId = id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey))
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.IdempotencyKey), "Idempotency key is required."));
        if (context.IdempotencyKey.Trim().Length > 100)
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.IdempotencyKey), "Idempotency key cannot exceed 100 characters."));
        if (context.ExpectedVersion < 1)
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.ExpectedVersion), "Expected version must be at least 1."));
        var channel = context.ClientChannel?.Trim().ToUpperInvariant() ?? string.Empty;
        if (channel is not ("MES" or "MOBILE" or "POP"))
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var actor = User(context.User);
        var deviceId = Trimmed(context.DeviceId);
        var remark = Trimmed(context.Remark);
        if (actor.Length > PomStorageBoundary.ActorLength)
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.User), $"User cannot exceed {PomStorageBoundary.ActorLength} characters."));
        if (deviceId?.Length > PomStorageBoundary.DeviceIdLength)
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.DeviceId), $"Device ID cannot exceed {PomStorageBoundary.DeviceIdLength} characters."));
        if (remark?.Length > PomStorageBoundary.ReasonLength)
            return Result.Failure<PomWorkOrder>(Error.Validation(
                nameof(context.Remark), $"Remark cannot exceed {PomStorageBoundary.ReasonLength} characters."));

        var workOrder = await _workOrders.GetByIdAsync(normalizedId, ct);
        if (workOrder is null)
            return Result.Failure<PomWorkOrder>(Error.NotFoundOf(nameof(PomWorkOrder), normalizedId));

        // 같은 키의 재전송은 동일 업무 요청이면 현재 상태를 반환하고, 다른 요청에 키를 재사용하면 충돌로 거부한다.
        var key = context.IdempotencyKey.Trim();
        var prior = await _workOrders.GetExecutionByIdempotencyKeyAsync(key, ct);
        if (prior is not null)
        {
            return SameRequest(
                    prior, workOrder.Id, action, goodQty, defectQty,
                    actor, channel, deviceId, remark, context.ExpectedVersion)
                ? Result.Success(workOrder)
                : Result.Failure<PomWorkOrder>(Error.Conflict(
                    $"Idempotency key '{key}' was already used for a different work-order operation."));
        }

        // 사전 검사는 사용자에게 현재 버전을 알려 주기 위한 빠른 경로다. 실제 경합 판정은
        // 저장소의 UPDATE ... WHERE VERSION_NO 조건이 트랜잭션 안에서 최종 보장한다.
        if (workOrder.VersionNo != context.ExpectedVersion)
            return Result.Failure<PomWorkOrder>(Error.Conflict(
                $"Work order was changed concurrently. Current version: {workOrder.VersionNo}."));

        // Route-bound work orders are executed only through LOT TrackIn/TrackOut. Direct actions
        // would bypass LOT traceability, predecessor enforcement, and per-process quality gates.
        if (workOrder.IsRoutingBound &&
            action is PomWorkOrderAction.Start or PomWorkOrderAction.Report or PomWorkOrderAction.Complete)
            return Result.Failure<PomWorkOrder>(Error.Conflict(
                "ROUTE_BOUND_LOT_EXECUTION_REQUIRED: start, report, and complete this work order through LOT execution."));

        if (precondition is not null)
        {
            var allowed = await precondition(workOrder, ct);
            if (allowed.IsFailure)
                return Result.Failure<PomWorkOrder>(allowed.Error);
        }

        // 같은 Complete 요청의 정확한 재시도는 위의 실행 이력 검사에서 이미 성공으로 반환된다.
        // 따라서 새 완료 전이에만 LOT/QMS를 조회해, 성공했던 요청이 이후의 품질 상태 변화로 실패하지 않게 한다.
        if (action == PomWorkOrderAction.Complete)
        {
            var completionGate = await ValidateCompletionGateAsync(
                workOrder, goodQty ?? 0m, defectQty ?? 0m, ct);
            if (completionGate.IsFailure)
                return Result.Failure<PomWorkOrder>(completionGate.Error);
        }

        var from = workOrder.Status;
        var changed = mutation(workOrder);
        if (changed.IsFailure) return Result.Failure<PomWorkOrder>(changed.Error);

        var now = DateTime.UtcNow;
        var execution = new PomWorkOrderExecution(
            Guid.NewGuid().ToString("N"), workOrder.Id, key, action, from, workOrder.Status,
            goodQty, defectQty, actor, workOrder.EquipmentId,
            channel, deviceId, now, remark,
            ExpectedVersion: context.ExpectedVersion,
            ResultVersion: context.ExpectedVersion + 1);

        // 상태 행과 append-only 실행 이력을 함께 저장해야 성공 응답과 감사 기록이 서로 어긋나지 않는다.
        var persisted = await _workOrders.UpdateWithExecutionAsync(workOrder, execution, ct);
        if (!persisted)
        {
            // 두 단말이 같은 멱등 요청을 동시에 보낸 경우 뒤늦은 버전 경합을 정상 재시도로 복원한다.
            var concurrentExecution = await _workOrders.GetExecutionByIdempotencyKeyAsync(key, ct);
            if (concurrentExecution is not null &&
                SameRequest(
                    concurrentExecution, normalizedId, action, goodQty, defectQty,
                    actor, channel, deviceId, remark, context.ExpectedVersion))
            {
                var current = await _workOrders.GetByIdAsync(normalizedId, ct);
                if (current is not null) return Result.Success(current);
            }
            return Result.Failure<PomWorkOrder>(Error.Conflict(
                "Work order was changed concurrently. Reload and retry."));
        }
        return Result.Success(workOrder);
    }

    /// <summary>
    /// 작업지시에 연결된 LOT 때문에 수동 완료가 최종 TrackOut 품질 경계를 우회하지 않는지 확인한다.
    /// LOT을 사용하지 않는 작업지시는 기존 수기 실적 마감 흐름을 유지한다.
    /// </summary>
    private async Task<Result> ValidateCompletionGateAsync(
        PomWorkOrder workOrder,
        decimal requestedGoodQty,
        decimal requestedDefectQty,
        CancellationToken ct)
    {
        var lots = await _lots.GetByWorkOrderAsync(workOrder.Id, ct) ?? [];
        if (lots.Count == 0)
            return Result.Success();

        // Hold는 공정 위치와 관계없는 명시적 생산 중지 신호다. 한 LOT이라도 보류 중이면 W/O 마감도 금지한다.
        var held = lots.FirstOrDefault(lot => lot.IsHold);
        if (held is not null)
            return Result.Failure(Error.Conflict(
                $"Work order completion is blocked because linked lot '{held.Id}' is on Hold."));

        // 자동 완료(PrepareFinishWorkOrderAsync)와 같은 종결 조건을 적용한다. Mixing에 투입된 Consumed LOT은
        // 완제품 LOT은 아니지만 이미 되돌릴 수 없는 정상 종결 상태이므로 Completed와 함께 허용한다.
        var unfinished = lots.FirstOrDefault(lot => lot.State is not (LotState.Completed or LotState.Consumed));
        if (unfinished is not null)
            return Result.Failure(Error.Conflict(
                $"Work order completion is blocked because linked lot '{unfinished.Id}' is not completed " +
                $"(current state: {unfinished.State})."));

        // 연결 LOT이 있으면 append-only 추적 데이터가 실적의 기준이다. 수동 payload가 자동 완료 집계와
        // 다르면 LOT 추적성과 W/O 누계가 갈라지므로 조용히 덮어쓰지 않고 재조회가 필요한 충돌로 반환한다.
        var completedLots = lots.Where(lot => lot.State == LotState.Completed).ToList();
        var lotGoodQty = completedLots.Sum(lot => Math.Max(0m, lot.Qty - lot.DefectQty));
        var lotDefectQty = completedLots.Sum(lot => lot.DefectQty);
        if (requestedGoodQty != lotGoodQty || requestedDefectQty != lotDefectQty)
            return Result.Failure(Error.Conflict(
                $"Work order completion quantities must match completed linked lots " +
                $"(good: {lotGoodQty}, defect: {lotDefectQty})."));

        foreach (var lot in completedLots)
        {
            // Completed LOT의 CurrentProcessId는 마지막 처리 공정을 유지하므로 그 공정의 최종 증거를 재검증한다.
            var processId = lot.CurrentProcessId;
            if (string.IsNullOrWhiteSpace(processId))
                return Result.Failure(Error.Conflict(
                    $"Work order completion is blocked because linked lot '{lot.Id}' has no process for quality evaluation."));

            var quality = await _productionQuality.EvaluateAsync(lot.Id, processId, workOrder.Id, ct);
            if (quality.AllowsCompletion)
                continue;

            var blockingSpec = string.IsNullOrWhiteSpace(quality.BlockingSpecId)
                ? string.Empty
                : $" Blocking specification: {quality.BlockingSpecId}.";
            return Result.Failure(Error.Conflict(
                $"Production quality gate for linked lot '{lot.Id}' is {quality.Status}; " +
                $"work order completion is blocked.{blockingSpec}"));
        }

        return Result.Success();
    }

    /// <summary>기존 실행 이력이 동일 작업지시·동작·실적 payload를 의미하는지 비교한다.</summary>
    private static bool SameRequest(
        PomWorkOrderExecution execution, string workOrderId, PomWorkOrderAction action,
        decimal? goodQty, decimal? defectQty, string user, string clientChannel,
        string? deviceId, string? remark, int expectedVersion)
        => string.Equals(execution.WorkOrderId, workOrderId, StringComparison.OrdinalIgnoreCase)
           && execution.Action == action
           && execution.GoodQty == goodQty
           && execution.DefectQty == defectQty
           && string.Equals(execution.UserId, user, StringComparison.OrdinalIgnoreCase)
           && string.Equals(execution.ClientChannel, clientChannel, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Trimmed(execution.DeviceId), deviceId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Trimmed(execution.Remark), remark, StringComparison.Ordinal)
           && execution.ExpectedVersion == expectedVersion
           && execution.ResultVersion == expectedVersion + 1;

    /// <summary>감사 사용자 공백을 시스템 사용자로 정규화한다.</summary>
    private static string User(string? value) => string.IsNullOrWhiteSpace(value) ? "SYSTEM" : value.Trim();

    /// <summary>선택 문자열을 잘라내고 공백 값은 null로 통일한다.</summary>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
