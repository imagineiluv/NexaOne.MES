using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Cmms;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 CMMS 보전 엔드포인트(ADR-008 얇은 브리지). plugin-ALC CmmsService/MaintenancePlanService를
/// ICmmsBridge로 호출한다. 쓰기(작업지시·보전계획 생명주기, 예비품 생성/재고조정)는 cmms:manage 수동 검사.
/// Result→HTTP(BridgeResultExtensions: Conflict→409·NotFound→404·Validation→400·성공→200/204). (modules ON에서만 동작.)
/// 순수 조회는 게이트웨이(/api/v1/query/CMMS.*)로, Plan→WO 캐스케이드(다중 애그리거트)는 보류한다.</summary>
[ApiController]
[Route("api/v1/cmms")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class CmmsBridgeController : ControllerBase
{
    private readonly ICmmsBridge _bridge;
    public CmmsBridgeController(ICmmsBridge bridge) => _bridge = bridge;

    // ── 작업지시(WorkOrder) ──

    [HttpPost("work-orders")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateWorkOrder([FromBody] CreateWorkOrderRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CreateWorkOrderAsync(
            req.WoId, req.EquipmentId, req.WoType, req.Description, req.AssigneeId, ct)).ToActionResult();
    }

    [HttpPost("work-orders/{woId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartWorkOrder(string woId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.StartWorkOrderAsync(woId, ct)).ToActionResult();
    }

    [HttpPost("work-orders/{woId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CompleteWorkOrder(string woId, [FromBody] CompleteWorkOrderRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CompleteWorkOrderAsync(woId, req.Remark, ct)).ToActionResult();
    }

    [HttpPost("work-orders/{woId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CancelWorkOrder(string woId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CancelWorkOrderAsync(woId, ct)).ToActionResult();
    }

    // ── 보전계획(MaintenancePlan) ──

    [HttpPost("plans")]
    [ProducesResponseType<MaintenancePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePlan([FromBody] CreateMaintenancePlanRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CreatePlanAsync(
            req.PlanId, req.PlanName, req.EquipmentId, req.PlanType, req.CycleType,
            req.ScheduledDate, req.EstimatedHours, req.AssigneeId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartPlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.StartPlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CompletePlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CompletePlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CancelPlan(string planId, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CancelPlanAsync(planId, ct)).ToActionResult();
    }

    // ── 예비품(SparePart) ──

    [HttpPost("spare-parts")]
    [ProducesResponseType<SparePartDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePart([FromBody] CreateSparePartRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.CreatePartAsync(
            req.PartId, req.PartName, req.PartNumber, req.Description, req.UnitOfMeasure,
            req.CurrentStock, req.MinStock, req.MaxStock, req.Location, req.EquipmentClassId, ct)).ToActionResult();
    }

    [HttpPost("spare-parts/{partId}/adjust-stock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AdjustStock(string partId, [FromBody] AdjustStockRequest req, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.CmmsManage)) return Forbid();
        return (await _bridge.AdjustStockAsync(partId, req.Delta, ct)).ToActionResult();
    }

}

public record CreateWorkOrderRequest(string WoId, string EquipmentId, string WoType, string Description, string AssigneeId);
public record CompleteWorkOrderRequest(string Remark);
public record CreateMaintenancePlanRequest(
    string PlanId, string PlanName, string EquipmentId, string PlanType, string CycleType,
    DateTime ScheduledDate, decimal EstimatedHours, string AssigneeId);
public record CreateSparePartRequest(
    string PartId, string PartName, string PartNumber, string Description, string UnitOfMeasure,
    decimal CurrentStock, decimal MinStock, decimal MaxStock, string Location, string? EquipmentClassId);
public record AdjustStockRequest(decimal Delta);
