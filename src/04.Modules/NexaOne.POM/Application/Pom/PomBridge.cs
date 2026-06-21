using NexaOne.Common;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.Pom;

/// <summary>ADR-008 얇은 브리지 어댑터 — PomService(생산계획)·ProductionOrderService(생산오더)·
/// LotTrackingService(Lot 추적)에 위임하고 도메인 엔티티를 계약 DTO로 매핑(Status/State enum→string).
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IPomBridge로 캐스트해 DI에 등록한다. 상태전이/팩토리
/// 검증의 Result는 그대로 통과시켜 컨트롤러가 409/400/404로 매핑한다. Lot Mixing(다중 애그리거트)은
/// 본 어댑터에서 노출하지 않는다(UnitOfWork 선결).</summary>
public sealed class PomBridge : IPomBridge
{
    private readonly PomService _planService;
    private readonly ProductionOrderService _orderService;
    private readonly LotTrackingService _lotService;

    public PomBridge(PomService planService, ProductionOrderService orderService, LotTrackingService lotService)
    {
        _planService = planService;
        _orderService = orderService;
        _lotService = lotService;
    }

    // ── 생산계획 ──

    public async Task<Result<ProductionPlanDto>> CreatePlanAsync(
        string planId, string planName, string plantId, string productId, decimal plannedQty,
        DateTime plannedStartDate, DateTime plannedEndDate, CancellationToken ct = default)
    {
        var r = await _planService.CreatePlanAsync(
            planId, planName, plantId, productId, plannedQty, plannedStartDate, plannedEndDate, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ProductionPlanDto>(r.Error);
    }

    public Task<Result> ReleasePlanAsync(string planId, CancellationToken ct = default)
        => _planService.ReleasePlanAsync(planId, ct);

    public Task<Result> StartPlanAsync(string planId, CancellationToken ct = default)
        => _planService.StartPlanAsync(planId, ct);

    public Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default)
        => _planService.CompletePlanAsync(planId, ct);

    public Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default)
        => _planService.CancelPlanAsync(planId, ct);

    // ── 생산오더 ──

    public async Task<Result<ProductionOrderDto>> CreateOrderAsync(
        string orderId, string planId, string equipmentId, string productId, decimal orderQty,
        DateTime scheduledStart, DateTime scheduledEnd, CancellationToken ct = default)
    {
        var r = await _orderService.CreateOrderAsync(
            orderId, planId, equipmentId, productId, orderQty, scheduledStart, scheduledEnd, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<ProductionOrderDto>(r.Error);
    }

    public Task<Result> StartOrderAsync(string orderId, CancellationToken ct = default)
        => _orderService.StartOrderAsync(orderId, ct);

    public Task<Result> CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default)
        => _orderService.CompleteOrderAsync(orderId, actualQty, ct);

    public Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default)
        => _orderService.CancelOrderAsync(orderId, ct);

    // ── Lot 추적(단일 애그리거트) ──

    public async Task<Result<LotDto>> CreateLotAsync(
        string plantId, string lotId, string? workOrderId, string productId,
        decimal qty, IReadOnlyList<string> routeSteps, string user, CancellationToken ct = default)
    {
        var r = await _lotService.CreateLotAsync(plantId, lotId, workOrderId, productId, qty, routeSteps, user, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<LotDto>(r.Error);
    }

    public async Task<Result<LotDto>> TrackInAsync(
        string plantId, string lotId, string equipmentId,
        string? recipeDefId, int? recipeDefVersion, string user, CancellationToken ct = default)
    {
        var r = await _lotService.TrackInAsync(
            new TrackInCommand(plantId, lotId, equipmentId, recipeDefId, recipeDefVersion, user), ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<LotDto>(r.Error);
    }

    public async Task<Result<LotDto>> TrackOutAsync(
        string plantId, string lotId, string equipmentId, decimal qty,
        IReadOnlyList<LotDefectInput>? defects, string? carrierId, string user, CancellationToken ct = default)
    {
        var mapped = defects?.Select(d => new DefectEntry(d.DefectCode, d.DefectQty)).ToList();
        var r = await _lotService.TrackOutAsync(
            new TrackOutCommand(plantId, lotId, equipmentId, qty, mapped, carrierId, user), ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<LotDto>(r.Error);
    }

    public Task<Result> HoldLotAsync(string lotId, string user, CancellationToken ct = default)
        => _lotService.HoldAsync(lotId, user, ct);

    public Task<Result> ReleaseLotHoldAsync(string lotId, string user, CancellationToken ct = default)
        => _lotService.ReleaseHoldAsync(lotId, user, ct);

    // ── 매핑 ──

    private static ProductionPlanDto ToDto(ProductionPlan p)
        => new(p.Id, p.PlanName, p.PlantId, p.ProductId, p.PlannedQty,
            p.PlannedStartDate, p.PlannedEndDate, p.Status.ToString(), p.Remark);

    private static ProductionOrderDto ToDto(ProductionOrder o)
        => new(o.Id, o.PlanId, o.EquipmentId, o.ProductId, o.OrderQty, o.ActualQty,
            o.ScheduledStart, o.ScheduledEnd, o.ActualStart, o.ActualEnd, o.Status.ToString());

    private static LotDto ToDto(Lot l)
        => new(l.Id, l.PlantId, l.WorkOrderId, l.ProductId, l.Qty, l.DefectQty,
            l.State.ToString(), l.ProcessState.ToString(), l.RouteSteps, l.CurrentStepIndex,
            l.CurrentProcessId, l.EquipmentId, l.RecipeDefId, l.RecipeDefVersion, l.CarrierId, l.IsHold);
}
