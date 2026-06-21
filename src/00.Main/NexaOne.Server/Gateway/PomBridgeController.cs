using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 POM 생산 엔드포인트(ADR-008 얇은 브리지). plugin-ALC PomService/ProductionOrderService/
/// LotTrackingService를 IPomBridge로 호출한다. 쓰기(생산계획·생산오더 생명주기, Lot 추적 TrackIn/TrackOut/Hold/Release)는
/// pom:manage 수동 검사. Result→HTTP(BridgeResultExtensions: Conflict→409·NotFound→404·Validation→400·성공→200/204).
/// (modules ON에서만 동작.) 순수 조회는 게이트웨이(/api/v1/query/POM.*)로, Lot Mixing(다중 애그리거트)은 보류한다.</summary>
[ApiController]
[Route("api/v1/pom")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class PomBridgeController : ControllerBase
{
    private readonly IPomBridge _bridge;
    public PomBridgeController(IPomBridge bridge) => _bridge = bridge;

    // ── 생산계획(ProductionPlan) ──

    [HttpPost("plans")]
    [ProducesResponseType<ProductionPlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePlan([FromBody] CreateProductionPlanRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CreatePlanAsync(
            req.PlanId, req.PlanName, req.PlantId, req.ProductId, req.PlannedQty,
            req.PlannedStartDate, req.PlannedEndDate, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReleasePlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.ReleasePlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartPlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.StartPlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CompletePlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CompletePlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CancelPlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CancelPlanAsync(planId, ct)).ToActionResult();
    }

    // ── 생산오더(ProductionOrder) ──

    [HttpPost("orders")]
    [ProducesResponseType<ProductionOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateProductionOrderRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CreateOrderAsync(
            req.OrderId, req.PlanId, req.EquipmentId, req.ProductId, req.OrderQty,
            req.ScheduledStart, req.ScheduledEnd, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartOrder(string orderId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.StartOrderAsync(orderId, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CompleteOrder(string orderId, [FromBody] CompleteProductionOrderRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CompleteOrderAsync(orderId, req.ActualQty, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CancelOrderAsync(orderId, ct)).ToActionResult();
    }

    // ── Lot 추적(단일 애그리거트) ──
    // Lot Mixing(다중 애그리거트 소비/병합)은 의도적으로 노출하지 않는다(UnitOfWork 선결).

    [HttpPost("lots")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateLot([FromBody] CreateLotRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.CreateLotAsync(
            req.PlantId, req.LotId, req.WorkOrderId, req.ProductId, req.Qty, req.RouteSteps ?? [], CurrentUserId, ct))
            .ToActionResult();
    }

    [HttpPost("lots/{lotId}/track-in")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> TrackIn(string lotId, [FromBody] TrackInRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.TrackInAsync(
            req.PlantId, lotId, req.EquipmentId, req.RecipeDefId, req.RecipeDefVersion, CurrentUserId, ct))
            .ToActionResult();
    }

    [HttpPost("lots/{lotId}/track-out")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> TrackOut(string lotId, [FromBody] TrackOutRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.TrackOutAsync(
            req.PlantId, lotId, req.EquipmentId, req.Qty, req.Defects, req.CarrierId, CurrentUserId, ct))
            .ToActionResult();
    }

    [HttpPost("lots/{lotId}/hold")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> HoldLot(string lotId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.HoldLotAsync(lotId, CurrentUserId, ct)).ToActionResult();
    }

    [HttpPost("lots/{lotId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReleaseLot(string lotId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.PomManage)) return Forbid();
        return (await _bridge.ReleaseLotHoldAsync(lotId, CurrentUserId, ct)).ToActionResult();
    }

    private string CurrentUserId => User.CurrentUserId() ?? "SYSTEM";
}

public record CreateProductionPlanRequest(
    string PlanId, string PlanName, string PlantId, string ProductId, decimal PlannedQty,
    DateTime PlannedStartDate, DateTime PlannedEndDate);
public record CreateProductionOrderRequest(
    string OrderId, string PlanId, string EquipmentId, string ProductId, decimal OrderQty,
    DateTime ScheduledStart, DateTime ScheduledEnd);
public record CompleteProductionOrderRequest(decimal ActualQty);
public record CreateLotRequest(
    string PlantId, string LotId, string? WorkOrderId, string ProductId, decimal Qty, IReadOnlyList<string>? RouteSteps);
public record TrackInRequest(string PlantId, string EquipmentId, string? RecipeDefId, int? RecipeDefVersion);
public record TrackOutRequest(
    string PlantId, string EquipmentId, decimal Qty, IReadOnlyList<LotDefectInput>? Defects, string? CarrierId);
