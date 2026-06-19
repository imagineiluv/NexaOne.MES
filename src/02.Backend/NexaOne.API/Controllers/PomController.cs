using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Extensions;
using NexaOne.Common;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/pom")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public class PomController(PomService ppmService, ProductionOrderService orderService) : ControllerBase
{
    [HttpGet("plans")]
    [ProducesResponseType<IReadOnlyList<ProductionPlan>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPlans(
        [FromQuery] string plantId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var result = await ppmService.GetByPlantAsync(plantId, from, to, ct);
        return result.ToActionResult();
    }

    [HttpPost("plans")]
    [Authorize(Policy = "perm:pom:manage")]   // ADR-003 — 생산 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    [ProducesResponseType<ProductionPlan>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreatePlan([FromBody] CreatePlanRequest req, CancellationToken ct)
    {
        var result = await ppmService.CreatePlanAsync(
            req.PlanId, req.PlanName, req.PlantId, req.ProductId,
            req.Qty, req.StartDate, req.EndDate, ct);
        return result.ToActionResult();
    }

    [HttpPut("plans/{planId}/start")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartPlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.StartPlanAsync(planId, ct);
        return result.ToActionResult();
    }

    [HttpPut("plans/{planId}/release")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReleasePlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.ReleasePlanAsync(planId, ct);
        return result.ToActionResult();
    }

    [HttpPut("plans/{planId}/complete")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompletePlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.CompletePlanAsync(planId, ct);
        return result.ToActionResult();
    }

    [HttpPut("plans/{planId}/cancel")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelPlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.CancelPlanAsync(planId, ct);
        return result.ToActionResult();
    }
    // ── Orders ────────────────────────────────────────────────────────────────

    [HttpGet("orders")]
    [ProducesResponseType<IReadOnlyList<ProductionOrder>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrders([FromQuery] string planId, CancellationToken ct)
    {
        var result = await orderService.GetByPlanAsync(planId, ct);
        return result.ToActionResult();
    }

    [HttpPost("orders")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType<ProductionOrder>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateProductionOrderRequest req, CancellationToken ct)
    {
        var result = await orderService.CreateOrderAsync(
            req.OrderId, req.PlanId, req.EquipmentId, req.ProductId,
            req.OrderQty, req.ScheduledStart, req.ScheduledEnd, ct);
        return result.ToActionResult();
    }

    [HttpPut("orders/{orderId}/start")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartOrder(string orderId, CancellationToken ct)
    {
        var result = await orderService.StartOrderAsync(orderId, ct);
        return result.ToActionResult();
    }

    [HttpPut("orders/{orderId}/complete")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompleteOrder(string orderId, [FromBody] CompleteOrderRequest req, CancellationToken ct)
    {
        var result = await orderService.CompleteOrderAsync(orderId, req.ActualQty, ct);
        return result.ToActionResult();
    }

    [HttpPut("orders/{orderId}/cancel")]
    [Authorize(Policy = "perm:pom:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        var result = await orderService.CancelOrderAsync(orderId, ct);
        return result.ToActionResult();
    }
}

public record CreatePlanRequest(string PlanId, string PlanName, string PlantId, string ProductId, decimal Qty, DateTime StartDate, DateTime EndDate);
public record CreateProductionOrderRequest(string OrderId, string PlanId, string EquipmentId, string ProductId, decimal OrderQty, DateTime ScheduledStart, DateTime ScheduledEnd);
public record CompleteOrderRequest(decimal ActualQty);
