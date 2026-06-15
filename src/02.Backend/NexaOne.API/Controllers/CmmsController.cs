using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Hubs;
using NexaOne.CMMS.Application.Cmms;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/cmms")]
[Authorize]
public class CmmsController(CmmsService emsService, IEesHubNotifier notifier,
    MaintenancePlanService planService,
    NexaOne.API.Services.RealtimeNotificationCoordinator notifyCoordinator) : ControllerBase
{
    [HttpGet("work-orders")]
    public async Task<IActionResult> GetWorkOrders(
        [FromQuery] string? equipmentId, [FromQuery] string? status,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<NexaOne.CMMS.Domain.WorkOrderStatus>(status, out var woStatus))
        {
            var byStatus = await emsService.GetByStatusAsync(woStatus, ct);
            return byStatus.IsSuccess ? Ok(byStatus.Value) : BadRequest(byStatus.Error);
        }
        var id = equipmentId ?? string.Empty;
        var result = await emsService.GetByEquipmentAsync(id, from, to, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("work-orders")]
    [Authorize(Policy = "perm:cmms:manage")]   // ADR-003 — 보전 쓰기는 모듈 manage 권한 필요(기본 ADMIN)
    public async Task<IActionResult> CreateWorkOrder([FromBody] CreateWorkOrderRequest req, CancellationToken ct)
    {
        var result = await emsService.CreateWorkOrderAsync(
            req.WoId, req.EquipmentId, req.WoType, req.Description, req.AssigneeId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("work-orders/{woId}/start")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> StartWorkOrder(string woId, CancellationToken ct)
    {
        var result = await emsService.StartWorkOrderAsync(woId, ct);
        if (result.IsSuccess && !notifyCoordinator.BusDeliversEvents)
            await notifier.NotifyWorkOrderUpdatedAsync(ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("work-orders/{woId}/complete")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> CompleteWorkOrder(string woId, [FromBody] CompleteWorkOrderRequest req, CancellationToken ct)
    {
        var result = await emsService.CompleteWorkOrderAsync(woId, req.Remark, ct);
        if (result.IsSuccess && !notifyCoordinator.BusDeliversEvents)
            await notifier.NotifyWorkOrderUpdatedAsync(ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("work-orders/{woId}/cancel")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> CancelWorkOrder(string woId, CancellationToken ct)
    {
        var result = await emsService.CancelWorkOrderAsync(woId, ct);
        if (result.IsSuccess && !notifyCoordinator.BusDeliversEvents)
            await notifier.NotifyWorkOrderUpdatedAsync(ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── Maintenance Plans ─────────────────────────────────────────────────────

    [HttpGet("maintenance-plans")]
    public async Task<IActionResult> GetMaintenancePlans([FromQuery] string? equipmentId,
        [FromQuery] string? status, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(status) &&
            Enum.TryParse<NexaOne.CMMS.Domain.MaintenancePlanStatus>(status, out var s))
        {
            var byStatus = await planService.GetByStatusAsync(s, ct);
            return Ok(byStatus);
        }
        var list = await planService.GetByEquipmentAsync(equipmentId ?? string.Empty, ct);
        return Ok(list);
    }

    [HttpPost("maintenance-plans")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> CreateMaintenancePlan([FromBody] CreateMaintenancePlanRequest req, CancellationToken ct)
    {
        var result = await planService.CreatePlanAsync(req.PlanId, req.PlanName, req.EquipmentId,
            req.PlanType, req.CycleType, req.ScheduledDate, req.EstimatedDurationHours, req.AssigneeId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("maintenance-plans/{planId}/start")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> StartMaintenancePlan(string planId, CancellationToken ct)
    {
        var result = await planService.StartPlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("maintenance-plans/{planId}/complete")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> CompleteMaintenancePlan(string planId, CancellationToken ct)
    {
        var result = await planService.CompletePlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpPut("maintenance-plans/{planId}/cancel")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> CancelMaintenancePlan(string planId, CancellationToken ct)
    {
        var result = await planService.CancelPlanAsync(planId, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    // ── Spare Parts ───────────────────────────────────────────────────────────

    [HttpGet("spare-parts")]
    public async Task<IActionResult> GetSpareParts([FromQuery] bool lowStock = false, CancellationToken ct = default)
    {
        var list = lowStock
            ? await planService.GetLowStockPartsAsync(ct)
            : await planService.GetPartsAsync(ct);
        return Ok(list);
    }

    [HttpPost("spare-parts")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> CreateSparePart([FromBody] CreateSparePartRequest req, CancellationToken ct)
    {
        var result = await planService.CreatePartAsync(req.PartId, req.PartName, req.PartNumber,
            req.Description, req.UnitOfMeasure, req.CurrentStock, req.MinStock, req.MaxStock,
            req.Location, req.EquipmentClassId, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("spare-parts/{partId}/adjust-stock")]
    [Authorize(Policy = "perm:cmms:manage")]
    public async Task<IActionResult> AdjustStock(string partId, [FromBody] AdjustStockRequest req, CancellationToken ct)
    {
        var result = await planService.AdjustStockAsync(partId, req.Delta, ct);
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }
}

public record CreateWorkOrderRequest(string WoId, string EquipmentId, string WoType, string Description, string AssigneeId);
public record CompleteWorkOrderRequest(string Remark);
public record CreateMaintenancePlanRequest(string PlanId, string PlanName, string EquipmentId,
    string PlanType, string CycleType, DateTime ScheduledDate, decimal EstimatedDurationHours, string AssigneeId);
public record CreateSparePartRequest(string PartId, string PartName, string PartNumber, string Description,
    string UnitOfMeasure, decimal CurrentStock, decimal MinStock, decimal MaxStock,
    string Location, string? EquipmentClassId);
public record AdjustStockRequest(decimal Delta);
