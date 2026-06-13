using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.POM.Application.Pom;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/pom")]
[Authorize]
public class PomController(PomService ppmService, ProductionOrderService orderService) : ControllerBase
{
    [HttpGet("plans")]
    public async Task<IActionResult> GetPlans(
        [FromQuery] string plantId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var result = await ppmService.GetByPlantAsync(plantId, from, to, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("plans")]
    [Authorize(Policy = "perm:pom:manage")]   // ADR-003 — 생산 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    public async Task<IActionResult> CreatePlan([FromBody] CreatePlanRequest req, CancellationToken ct)
    {
        var result = await ppmService.CreatePlanAsync(
            req.PlanId, req.PlanName, req.PlantId, req.ProductId,
            req.Qty, req.StartDate, req.EndDate, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("plans/{planId}/start")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> StartPlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.StartPlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("plans/{planId}/release")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> ReleasePlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.ReleasePlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("plans/{planId}/complete")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> CompletePlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.CompletePlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("plans/{planId}/cancel")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> CancelPlan(string planId, CancellationToken ct)
    {
        var result = await ppmService.CancelPlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }
    // ── Orders ────────────────────────────────────────────────────────────────

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders([FromQuery] string planId, CancellationToken ct)
    {
        var result = await orderService.GetByPlanAsync(planId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("orders")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> CreateOrder([FromBody] CreateProductionOrderRequest req, CancellationToken ct)
    {
        var result = await orderService.CreateOrderAsync(
            req.OrderId, req.PlanId, req.EquipmentId, req.ProductId,
            req.OrderQty, req.ScheduledStart, req.ScheduledEnd, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("orders/{orderId}/start")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> StartOrder(string orderId, CancellationToken ct)
    {
        var result = await orderService.StartOrderAsync(orderId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("orders/{orderId}/complete")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> CompleteOrder(string orderId, [FromBody] CompleteOrderRequest req, CancellationToken ct)
    {
        var result = await orderService.CompleteOrderAsync(orderId, req.ActualQty, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("orders/{orderId}/cancel")]
    [Authorize(Policy = "perm:pom:manage")]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        var result = await orderService.CancelOrderAsync(orderId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }
}

public record CreatePlanRequest(string PlanId, string PlanName, string PlantId, string ProductId, decimal Qty, DateTime StartDate, DateTime EndDate);
public record CreateProductionOrderRequest(string OrderId, string PlanId, string EquipmentId, string ProductId, decimal OrderQty, DateTime ScheduledStart, DateTime ScheduledEnd);
public record CompleteOrderRequest(decimal ActualQty);
