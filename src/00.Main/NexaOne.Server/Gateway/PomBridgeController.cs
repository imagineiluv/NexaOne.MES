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
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CreatePlan([FromBody] CreateProductionPlanRequest req, CancellationToken ct)
    {
        return (await _bridge.CreatePlanAsync(
            req.PlanId, req.PlanName, req.PlantId, req.ProductId, req.PlannedQty,
            req.PlannedStartDate, req.PlannedEndDate, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> ReleasePlan(string planId, CancellationToken ct)
    {
        return (await _bridge.ReleasePlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> StartPlan(string planId, CancellationToken ct)
    {
        return (await _bridge.StartPlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CompletePlan(string planId, CancellationToken ct)
    {
        return (await _bridge.CompletePlanAsync(planId, ct)).ToActionResult();
    }

    [HttpPost("plans/{planId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CancelPlan(string planId, CancellationToken ct)
    {
        return (await _bridge.CancelPlanAsync(planId, ct)).ToActionResult();
    }

    // ── 생산오더(ProductionOrder) ──

    [HttpPost("orders")]
    [ProducesResponseType<ProductionOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateProductionOrderRequest req, CancellationToken ct)
    {
        return (await _bridge.CreateOrderAsync(
            req.OrderId, req.PlanId, req.EquipmentId, req.ProductId, req.OrderQty,
            req.ScheduledStart, req.ScheduledEnd, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> StartOrder(string orderId, CancellationToken ct)
    {
        return (await _bridge.StartOrderAsync(orderId, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CompleteOrder(string orderId, [FromBody] CompleteProductionOrderRequest req, CancellationToken ct)
    {
        return (await _bridge.CompleteOrderAsync(orderId, req.ActualQty, ct)).ToActionResult();
    }

    [HttpPost("orders/{orderId}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CancelOrder(string orderId, CancellationToken ct)
    {
        return (await _bridge.CancelOrderAsync(orderId, ct)).ToActionResult();
    }

    // ── Lot 추적(단일 애그리거트) ──

    [HttpPost("lots")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> CreateLot([FromBody] CreateLotRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.CreateLotAsync(
            req.PlantId, req.LotId, req.WorkOrderId, req.ProductId, req.Qty, req.RouteSteps ?? [], actor, ct))
            .ToActionResult();
    }

    [HttpPost("lots/{lotId}/track-in")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> TrackIn(string lotId, [FromBody] TrackInRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (ValidateLotExecutionIdentity(req.ExpectedVersion, req.IdempotencyKey) is { } identityError)
            return BadRequest(identityError);
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.TrackInAsync(
            req.PlantId, lotId, req.EquipmentId, req.RecipeDefId, req.RecipeDefVersion, actor,
            req.ExpectedVersion, req.IdempotencyKey, channel, req.DeviceId, ct))
            .ToActionResult();
    }

    [HttpPost("lots/{lotId}/track-out")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> TrackOut(string lotId, [FromBody] TrackOutRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (ValidateLotExecutionIdentity(req.ExpectedVersion, req.IdempotencyKey) is { } identityError)
            return BadRequest(identityError);
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (req.Defects is { } defects)
        {
            decimal defectTotal = 0;
            foreach (var defect in defects)
            {
                if (defect is null)
                    return BadRequest(Error.Validation(nameof(req.Defects),
                        "Defect rows cannot be null."));
                try
                {
                    defectTotal += defect.DefectQty;
                }
                catch (OverflowException)
                {
                    return BadRequest(Error.Validation(nameof(req.Defects),
                        "Defect quantity total is outside the supported range."));
                }
            }

            if (defectTotal > req.Qty)
                return BadRequest(Error.Validation(nameof(req.Defects),
                    "Defect quantity total cannot exceed Track-Out quantity."));
        }
        return (await _bridge.TrackOutAsync(
            req.PlantId, lotId, req.EquipmentId, req.Qty, req.Defects, req.CarrierId, actor,
            req.ExpectedVersion, req.IdempotencyKey, channel, req.DeviceId, ct))
            .ToActionResult();
    }

    [HttpPost("lots/{lotId}/hold")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> HoldLot(
        string lotId, [FromQuery] int expectedVersion, [FromQuery] string idempotencyKey,
        CancellationToken ct,
        [FromQuery] string? reason = null, [FromQuery] string clientChannel = "MES",
        [FromQuery] string? deviceId = null)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (ValidateLotExecutionIdentity(expectedVersion, idempotencyKey) is { } identityError)
            return BadRequest(identityError);
        if (!TryNormalizeClientChannel(clientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.HoldLotAsync(
            lotId, actor, expectedVersion, idempotencyKey, reason, channel, deviceId, ct)).ToActionResult();
    }

    [HttpPost("lots/{lotId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomExecute)]
    public async Task<IActionResult> ReleaseLot(
        string lotId, [FromQuery] int expectedVersion, [FromQuery] string idempotencyKey,
        CancellationToken ct,
        [FromQuery] string? reason = null, [FromQuery] string clientChannel = "MES",
        [FromQuery] string? deviceId = null)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (ValidateLotExecutionIdentity(expectedVersion, idempotencyKey) is { } identityError)
            return BadRequest(identityError);
        if (!TryNormalizeClientChannel(clientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.ReleaseLotHoldAsync(
            lotId, actor, expectedVersion, idempotencyKey, reason, channel, deviceId, ct)).ToActionResult();
    }

    // ── LOT 라우팅 통제/예외 ──

    /// <summary>스캔한 LOT의 현재·다음 공정, 통제 모드, 재작업 복귀점 및 예외 요청을 반환합니다.</summary>
    [HttpGet("lots/{lotId}/routing-context")]
    [ProducesResponseType<LotRoutingContextDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRead)]
    public async Task<IActionResult> GetLotRoutingContext(string lotId, CancellationToken ct)
        => (await _bridge.GetLotRoutingContextAsync(lotId, ct)).ToActionResult();

    /// <summary>편차 적용 전에 Strict/Flexible/NoControl 정책을 서버에서 판정합니다.</summary>
    [HttpPost("lots/{lotId}/routing/evaluate")]
    [ProducesResponseType<RoutingPolicyDecisionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRoutingRequest)]
    public async Task<IActionResult> EvaluateLotRouting(
        string lotId, [FromBody] EvaluateLotRoutingRequest req, CancellationToken ct)
        => (await _bridge.EvaluateLotRoutingAsync(
            req.PlantId, lotId, req.DeviationType, req.TargetStepIndex,
            req.Reason, req.ExceptionId, ct)).ToActionResult();

    /// <summary>LOT별 라우팅 통제 모드를 변경합니다. 운영 정책 변경이므로 POM 관리 권한이 필요합니다.</summary>
    [HttpPost("lots/{lotId}/routing/control-mode")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> ChangeLotRoutingControlMode(
        string lotId, [FromBody] ChangeLotRoutingControlModeRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.ChangeLotRoutingControlModeAsync(
            req.PlantId, lotId, req.ControlMode, req.Reason, actor,
            req.ExpectedVersion, req.IdempotencyKey, channel,
            req.DeviceId, ct)).ToActionResult();
    }

    /// <summary>NoControl 편차 또는 승인된 Flexible 예외를 한 번만 적용하고 감사 이력을 남깁니다.</summary>
    [HttpPost("lots/{lotId}/routing/deviations")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRoutingRequest)]
    public async Task<IActionResult> ApplyLotRouteDeviation(
        string lotId, [FromBody] ApplyLotRouteDeviationRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.ApplyLotRouteDeviationAsync(
            req.PlantId, lotId, req.DeviationType, req.TargetStepIndex,
            req.Reason, actor, req.ExpectedVersion, req.IdempotencyKey,
            req.ExceptionId, channel, req.DeviceId, ct)).ToActionResult();
    }

    /// <summary>Flexible 라우팅 편차를 관리자 승인 대기 상태로 등록합니다.</summary>
    [HttpPost("lots/{lotId}/routing/exceptions")]
    [ProducesResponseType<RouteExceptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRoutingRequest)]
    public async Task<IActionResult> RequestLotRouteException(
        string lotId, [FromBody] RequestLotRouteExceptionRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ExceptionId))
            return BadRequest(Error.Validation(nameof(req.ExceptionId),
                "ExceptionId is required so retried requests remain idempotent."));
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        var exceptionId = req.ExceptionId.Trim();
        return (await _bridge.RequestLotRouteExceptionAsync(
            exceptionId, req.PlantId, lotId, req.DeviationType, req.TargetStepIndex,
            req.Reason, actor, req.ExpectedVersion, req.ExpiresAt,
            channel, req.DeviceId, ct)).ToActionResult();
    }

    [HttpGet("routing/exceptions/{exceptionId}")]
    [ProducesResponseType<RouteExceptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRead)]
    public async Task<IActionResult> GetLotRouteException(string exceptionId, CancellationToken ct)
        => (await _bridge.GetLotRouteExceptionAsync(exceptionId, ct)).ToActionResult();

    /// <summary>요청자와 분리된 승인자가 Flexible 라우팅 예외를 승인합니다.</summary>
    [HttpPost("routing/exceptions/{exceptionId}/approve")]
    [ProducesResponseType<RouteExceptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRoutingApprove)]
    public async Task<IActionResult> ApproveLotRouteException(
        string exceptionId, [FromBody] ReviewLotRouteExceptionRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.ApproveLotRouteExceptionAsync(
            exceptionId, actor, req.Reason, ct, channel, req.DeviceId)).ToActionResult();
    }

    /// <summary>승인 대기 중인 Flexible 라우팅 예외를 사유와 함께 반려합니다.</summary>
    [HttpPost("routing/exceptions/{exceptionId}/reject")]
    [ProducesResponseType<RouteExceptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomRoutingApprove)]
    public async Task<IActionResult> RejectLotRouteException(
        string exceptionId, [FromBody] ReviewLotRouteExceptionRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        if (!TryNormalizeClientChannel(req.ClientChannel, out var channel))
            return BadRequest(Error.Validation(nameof(req.ClientChannel), "Client channel must be MES, MOBILE, or POP."));
        return (await _bridge.RejectLotRouteExceptionAsync(
            exceptionId, actor, req.Reason, ct, channel, req.DeviceId)).ToActionResult();
    }

    // ── Lot Mixing(다중 애그리거트 소비/병합) — DATA-3 원자화로 전 문장 단일 트랜잭션 커밋 ──

    /// <summary>투입 Lot 소비 + 출력 Lot 생성/증량 + 관계/이력 기록을 한 번에 수행한다(§19.4 MixingLotTrackInOut).
    /// 부분 커밋 없음 — 검증 실패는 영속 전에 반환되고, 영속은 MixingPersistAsync 단일 트랜잭션이다.</summary>
    [HttpPost("lots/mixing/track-in-out")]
    [ProducesResponseType<LotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.PomManage)]
    public async Task<IActionResult> MixingTrackInOut([FromBody] MixingTrackInOutRequest req, CancellationToken ct)
    {
        if (!TryGetExternalActor(out var actor)) return Unauthorized();
        return (await _bridge.MixingTrackInOutAsync(
            req.PlantId, req.OutputLotId, req.ProductId, req.EquipmentId,
            req.OutputRouteSteps ?? [], req.Inputs ?? [], actor, ct))
            .ToActionResult();
    }

    private bool TryGetExternalActor(out string actor)
    {
        actor = User.CurrentUserId()?.Trim() ?? string.Empty;
        return actor.Length > 0;
    }

    private static bool TryNormalizeClientChannel(string? value, out string channel)
    {
        channel = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return channel is "MES" or "MOBILE" or "POP";
    }

    private static Error? ValidateLotExecutionIdentity(int expectedVersion, string? idempotencyKey)
    {
        if (expectedVersion < 1)
            return Error.Validation(nameof(expectedVersion), "Expected version must be at least 1.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error.Validation(nameof(idempotencyKey), "Idempotency key is required.");
        return idempotencyKey.Trim().Length <= 100
            ? null
            : Error.Validation(nameof(idempotencyKey), "Idempotency key cannot exceed 100 characters.");
    }
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
public record TrackInRequest(string PlantId, string EquipmentId, int ExpectedVersion, string IdempotencyKey,
    string? RecipeDefId = null, int? RecipeDefVersion = null,
    string ClientChannel = "MES", string? DeviceId = null);
public record TrackOutRequest(
    string PlantId, string EquipmentId, decimal Qty, int ExpectedVersion, string IdempotencyKey,
    IReadOnlyList<LotDefectInput>? Defects = null, string? CarrierId = null,
    string ClientChannel = "MES", string? DeviceId = null);
public record EvaluateLotRoutingRequest(
    string PlantId, string DeviationType, int TargetStepIndex, string? Reason = null, string? ExceptionId = null);
public record ChangeLotRoutingControlModeRequest(
    string PlantId, string ControlMode, string Reason, int ExpectedVersion, string IdempotencyKey,
    string ClientChannel = "MES", string? DeviceId = null);
public record ApplyLotRouteDeviationRequest(
    string PlantId, string DeviationType, int TargetStepIndex, string Reason,
    int ExpectedVersion, string IdempotencyKey, string? ExceptionId = null,
    string ClientChannel = "MES", string? DeviceId = null);
public record RequestLotRouteExceptionRequest(
    string PlantId, string DeviationType, int TargetStepIndex, string Reason,
    int ExpectedVersion, DateTime ExpiresAt, string ExceptionId,
    string ClientChannel = "MES", string? DeviceId = null);
public record ReviewLotRouteExceptionRequest(
    string? Reason = null, string ClientChannel = "MES", string? DeviceId = null);
public record MixingTrackInOutRequest(
    string PlantId, string OutputLotId, string ProductId, string EquipmentId,
    IReadOnlyList<string>? OutputRouteSteps, IReadOnlyList<MixingInputDto>? Inputs);
