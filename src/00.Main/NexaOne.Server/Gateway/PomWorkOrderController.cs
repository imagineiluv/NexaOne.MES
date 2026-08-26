using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 생산관리오더(<c>POM_PRODUCTION_ORDER</c>) 아래에서 실제 공정 실행을 담당하는 작업지시 API다.
/// 상태 변경은 모두 브리지로 위임하며, 컨트롤러는 HTTP 계약·사용자·권한 경계만 소유한다.
/// </summary>
[ApiController]
[Route("api/v1/pom/work-orders")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class PomWorkOrderController : ControllerBase
{
    private readonly IPomWorkOrderBridge _bridge;

    /// <summary>HTTP 요청을 POM 모듈의 작업지시 Bridge에 연결한다.</summary>
    public PomWorkOrderController(IPomWorkOrderBridge bridge) => _bridge = bridge;

    /// <summary>생산관리오더에 속한 실행 작업지시를 생성한다.</summary>
    [HttpPost]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> Create([FromBody] CreatePomWorkOrderRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CreateAsync(
            req.WorkOrderId, req.ProductionOrderId, req.PlantId, req.WorkOrderName,
            req.ProductId, req.PlanQty, req.PlanStartDate, req.PlanEndDate,
            req.ProcessId, req.EquipmentId, req.OwnerId, actor,
            req.RoutingId, req.RoutingStepNo, req.WorkCenterId, req.AreaId,
            req.WorkOrderType, req.SalesOrderId, req.Description, req.RoutingScope, ct)).ToActionResult();
    }

    /// <summary>작성된 작업지시를 실행 가능한 Released 상태로 전환한다.</summary>
    [HttpPost("{id}/release")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> Release(string id, [FromBody] PomWorkOrderOperationRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.ReleaseAsync(id, req.ExpectedVersion, actor, req.ClientChannel, req.IdempotencyKey,
            req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    /// <summary>현장 작업자가 Released 작업지시의 생산 실행을 시작한다.</summary>
    [HttpPost("{id}/start")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> Start(string id, [FromBody] PomWorkOrderOperationRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.StartAsync(id, req.ExpectedVersion, actor, req.ClientChannel, req.IdempotencyKey,
            req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    /// <summary>완료·불량 수량을 절대 누계로 보고해 동일 요청 재시도를 멱등하게 처리한다.</summary>
    [HttpPost("{id}/report")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> Report(string id, [FromBody] ReportPomWorkOrderRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.ReportAsync(id, req.GoodQty, req.DefectQty, req.ExpectedVersion, actor,
            req.ClientChannel, req.IdempotencyKey, req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    /// <summary>진행 중인 작업지시를 보류해 추가 생산 보고를 차단한다.</summary>
    [HttpPost("{id}/hold")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> Hold(string id, [FromBody] PomWorkOrderOperationRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.HoldAsync(id, req.ExpectedVersion, actor, req.ClientChannel, req.IdempotencyKey,
            req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    /// <summary>보류된 작업지시를 다시 실행 가능한 상태로 되돌린다.</summary>
    [HttpPost("{id}/release-hold")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> ReleaseHold(string id, [FromBody] PomWorkOrderOperationRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.ReleaseHoldAsync(id, req.ExpectedVersion, actor, req.ClientChannel, req.IdempotencyKey,
            req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    /// <summary>최종 생산·불량 수량을 확정하고 작업지시를 완료한다.</summary>
    [HttpPost("{id}/complete")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> Complete(string id, [FromBody] ReportPomWorkOrderRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CompleteAsync(id, req.GoodQty, req.DefectQty, req.ExpectedVersion, actor,
            req.ClientChannel, req.IdempotencyKey, req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    /// <summary>아직 생산을 시작하지 않은 작업지시를 취소한다.</summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType<PomWorkOrderDto>(StatusCodes.Status200OK)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> Cancel(string id, [FromBody] PomWorkOrderOperationRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CancelAsync(id, req.ExpectedVersion, actor, req.ClientChannel, req.IdempotencyKey,
            req.DeviceId, req.Remark, ct)).ToActionResult();
    }

    // 감사 대상 명령은 JWT에 작업자 식별자가 없으면 거부한다. SYSTEM 대체는 책임 추적성을 훼손한다.
    private bool TryGetExternalActor(out string actor)
    {
        actor = User.CurrentUserId()?.Trim() ?? string.Empty;
        return actor.Length > 0;
    }
}

/// <summary>생산관리오더 하위 작업지시 생성 요청.</summary>
public sealed record CreatePomWorkOrderRequest(
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
    string? RoutingId = null,
    int? RoutingStepNo = null,
    string? WorkCenterId = null,
    string? AreaId = null,
    string? WorkOrderType = null,
    string? SalesOrderId = null,
    string? Description = null,
    string? RoutingScope = null);

/// <summary>상태 전이 공통 요청. 버전과 멱등 키로 중복 실행 및 동시 수정을 차단한다.</summary>
public record PomWorkOrderOperationRequest(
    string IdempotencyKey,
    int ExpectedVersion,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? Remark = null);

/// <summary>생산실적 보고 요청. 양품과 불량 수량은 증분이 아닌 현재 절대 누계다.</summary>
public sealed record ReportPomWorkOrderRequest(
    decimal GoodQty,
    decimal DefectQty,
    string IdempotencyKey,
    int ExpectedVersion,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? Remark = null);
