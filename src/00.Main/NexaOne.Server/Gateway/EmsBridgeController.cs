using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 EMS 보전 엔드포인트(ADR-008 얇은 브리지). plugin-ALC EmsService/MaintenancePlanService를
/// IEmsBridge로 호출한다. 쓰기(작업지시·보전계획 생명주기, 예비품 생성/재고조정)는 ems:manage 수동 검사.
/// Result→HTTP(BridgeResultExtensions: Conflict→409·NotFound→404·Validation→400·성공→200/204). (modules ON에서만 동작.)
/// 순수 조회는 게이트웨이(/api/v1/query/EMS.*)로, Plan→WO 캐스케이드(다중 애그리거트)는 보류한다.</summary>
[ApiController]
[Route("api/v1/ems")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class EmsBridgeController : ControllerBase
{
    private readonly IEmsBridge _bridge;
    public EmsBridgeController(IEmsBridge bridge) => _bridge = bridge;

    // ── 작업지시(WorkOrder) ──

    [HttpPost("work-orders")]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CreateWorkOrder([FromBody] CreateWorkOrderRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CreateWorkOrderAsync(
            req.WoId, req.EquipmentId, req.WoType, req.Description, req.AssigneeId,
            req.MaintenancePlanId, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("work-orders/{woId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> StartWorkOrder(
        string woId,
        [FromBody] WorkOrderOperationRequest req,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.StartWorkOrderAsync(
            woId, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("work-orders/{woId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CompleteWorkOrder(string woId, [FromBody] CompleteWorkOrderRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CompleteWorkOrderAsync(
            woId, req.Remark, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("work-orders/{woId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CancelWorkOrder(
        string woId,
        [FromBody] WorkOrderOperationRequest req,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CancelWorkOrderAsync(
            woId, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    // ── 보전계획(MaintenancePlan) ──

    [HttpPost("plans")]
    [ProducesResponseType<MaintenancePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CreatePlan([FromBody] CreateMaintenancePlanRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CreatePlanAsync(
            req.PlanId, req.PlanName, req.EquipmentId, req.PlanType, req.CycleType,
            req.ScheduledDate, req.EstimatedHours, req.AssigneeId,
            Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> StartPlan(
        string planId,
        [FromBody] MaintenancePlanOperationRequest req,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.StartPlanAsync(
            planId, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CompletePlan(
        string planId,
        [FromBody] MaintenancePlanOperationRequest req,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CompletePlanAsync(
            planId, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CancelPlan(
        string planId,
        [FromBody] MaintenancePlanOperationRequest req,
        CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CancelPlanAsync(
            planId, Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    // ── 예비품(SparePart) ──

    [HttpPost("spare-parts")]
    [ProducesResponseType<SparePartDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> CreatePart([FromBody] CreateSparePartRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CreatePartAsync(
            req.PartId, req.PartName, req.PartNumber, req.Description, req.UnitOfMeasure,
            req.CurrentStock, req.MinStock, req.MaxStock, req.Location, req.EquipmentClassId,
            Command(actor, req.IdempotencyKey, req.ClientChannel,
                req.DeviceId, req.CorrelationId), ct)).ToActionResult();
    }

    [HttpPost("spare-parts/{partId}/adjust-stock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> AdjustStock(string partId, [FromBody] AdjustStockRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        var adjustment = new SparePartAdjustmentDto(
            req.Delta,
            Command(actor, req.IdempotencyKey, req.ClientChannel, req.DeviceId, req.CorrelationId),
            req.TransactionType, req.WorkOrderId, req.EquipmentId,
            req.FromLocation, req.ToLocation, req.Remark, req.BomItemId);
        return (await _bridge.AdjustStockAsync(partId, adjustment, ct)).ToActionResult();
    }

    // 감사 대상 EMS 명령은 JWT에 사용자 식별자가 없으면 SYSTEM으로 대체하지 않고 닫힌 상태로 거부한다.
    private bool TryGetExternalActor(out string actor)
    {
        actor = User.CurrentUserId()?.Trim() ?? string.Empty;
        return actor.Length > 0;
    }

    private static EmsCommandContextDto Command(
        string actor,
        string? idempotencyKey,
        string? clientChannel,
        string? deviceId,
        string? correlationId) => new(
        actor,
        idempotencyKey?.Trim() ?? string.Empty,
        string.IsNullOrWhiteSpace(clientChannel) ? "MES" : clientChannel,
        deviceId,
        correlationId);
}

public record CreateWorkOrderRequest(
    string WoId,
    string EquipmentId,
    string WoType,
    string Description,
    string AssigneeId,
    string IdempotencyKey,
    string? MaintenancePlanId = null,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);
public record WorkOrderOperationRequest(
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);
public record CompleteWorkOrderRequest(
    string Remark,
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);
public record CreateMaintenancePlanRequest(
    string PlanId, string PlanName, string EquipmentId, string PlanType, string CycleType,
    DateTime ScheduledDate, decimal EstimatedHours, string AssigneeId,
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);
public record MaintenancePlanOperationRequest(
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null);
public record CreateSparePartRequest(
    string PartId, string PartName, string PartNumber, string Description, string UnitOfMeasure,
    decimal CurrentStock, decimal MinStock, decimal MaxStock, string Location, string? EquipmentClassId,
    string IdempotencyKey, string ClientChannel = "MES", string? DeviceId = null,
    string? CorrelationId = null);
public record AdjustStockRequest(
    decimal Delta,
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null,
    string? TransactionType = null,
    string? WorkOrderId = null,
    string? EquipmentId = null,
    string? FromLocation = null,
    string? ToLocation = null,
    string? Remark = null,
    string? BomItemId = null);
