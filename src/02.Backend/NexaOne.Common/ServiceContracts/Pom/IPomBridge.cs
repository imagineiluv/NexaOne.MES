using NexaOne.Common;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — POM 생산 단일 애그리거트 쓰기(생산계획 생명주기 +
/// 생산오더 생명주기 + Lot 추적 TrackIn/TrackOut/Hold/Release). plugin(POM)이 구현하고 호스트가
/// 주입된 ModuleBeanResolver와 typed DI composition seam을 통해 Default-ALC 계약으로 노출한다.
/// Result로 상태전이/팩토리 검증 분기(Conflict/Validation/
/// NotFound/Success)를 손실 없이 전달한다. 순수 조회는 게이트웨이(POM.xml)로. Lot Mixing(다중 애그리거트
/// 소비/병합)은 DATA-3 원자화(MixingPersistAsync 단일 트랜잭션)로 제외 사유가 해소되어 노출한다.</summary>
public interface IPomBridge : INexaModuleBridge
{
    // ── 생산계획(ProductionPlan) 생명주기 ──
    Task<Result<ProductionPlanDto>> CreatePlanAsync(
        string planId, string planName, string plantId, string productId, decimal plannedQty,
        DateTime plannedStartDate, DateTime plannedEndDate, CancellationToken ct = default);
    Task<Result> ReleasePlanAsync(string planId, CancellationToken ct = default);
    Task<Result> StartPlanAsync(string planId, CancellationToken ct = default);
    Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default);
    Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default);

    // ── 생산오더(ProductionOrder) 생명주기 ──
    Task<Result<ProductionOrderDto>> CreateOrderAsync(
        string orderId, string planId, string equipmentId, string productId, decimal orderQty,
        DateTime scheduledStart, DateTime scheduledEnd, CancellationToken ct = default);
    Task<Result> StartOrderAsync(string orderId, CancellationToken ct = default);
    Task<Result> CompleteOrderAsync(string orderId, decimal actualQty, CancellationToken ct = default);
    Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default);

    // ── Lot 추적(단일 애그리거트 상태전이) ──
    Task<Result<LotDto>> CreateLotAsync(
        string plantId, string lotId, string? workOrderId, string productId,
        decimal qty, IReadOnlyList<string> routeSteps, string user, CancellationToken ct = default);
    Task<Result<LotDto>> TrackInAsync(
        string plantId, string lotId, string equipmentId,
        string? recipeDefId, int? recipeDefVersion, string user,
        int expectedVersion, string idempotencyKey,
        string clientChannel = "MES", string? deviceId = null, CancellationToken ct = default);
    Task<Result<LotDto>> TrackOutAsync(
        string plantId, string lotId, string equipmentId, decimal qty,
        IReadOnlyList<LotDefectInput>? defects, string? carrierId, string user,
        int expectedVersion, string idempotencyKey,
        string clientChannel = "MES", string? deviceId = null, CancellationToken ct = default);
    Task<Result> HoldLotAsync(
        string lotId, string user, int expectedVersion, string idempotencyKey,
        string? reason = null, string clientChannel = "MES", string? deviceId = null,
        CancellationToken ct = default);
    Task<Result> ReleaseLotHoldAsync(
        string lotId, string user, int expectedVersion, string idempotencyKey,
        string? reason = null, string clientChannel = "MES", string? deviceId = null,
        CancellationToken ct = default);

    // ── LOT 라우팅 통제/예외 ──
    Task<Result<LotRoutingContextDto>> GetLotRoutingContextAsync(
        string lotId, CancellationToken ct = default);
    Task<Result<RoutingPolicyDecisionDto>> EvaluateLotRoutingAsync(
        string plantId, string lotId, string deviationType, int targetStepIndex,
        string? reason, string? exceptionId = null, CancellationToken ct = default);
    Task<Result<LotDto>> ChangeLotRoutingControlModeAsync(
        string plantId, string lotId, string controlMode, string reason, string user,
        int expectedVersion, string idempotencyKey, string clientChannel,
        string? deviceId = null, CancellationToken ct = default);
    Task<Result<LotDto>> ApplyLotRouteDeviationAsync(
        string plantId, string lotId, string deviationType, int targetStepIndex,
        string reason, string user, int expectedVersion, string idempotencyKey,
        string? exceptionId, string clientChannel, string? deviceId = null,
        CancellationToken ct = default);
    Task<Result<RouteExceptionDto>> RequestLotRouteExceptionAsync(
        string exceptionId, string plantId, string lotId, string deviationType,
        int targetStepIndex, string reason, string user, int expectedVersion,
        DateTime expiresAt, string clientChannel, string? deviceId = null,
        CancellationToken ct = default);
    Task<Result<RouteExceptionDto>> ApproveLotRouteExceptionAsync(
        string exceptionId, string reviewer, string? reason = null, CancellationToken ct = default,
        string clientChannel = "MES", string? deviceId = null);
    Task<Result<RouteExceptionDto>> RejectLotRouteExceptionAsync(
        string exceptionId, string reviewer, string? reason = null, CancellationToken ct = default,
        string clientChannel = "MES", string? deviceId = null);
    Task<Result<RouteExceptionDto>> GetLotRouteExceptionAsync(
        string exceptionId, CancellationToken ct = default);

    // ── Lot Mixing(다중 애그리거트 소비/병합) — DATA-3 원자화로 전 문장이 단일 트랜잭션 커밋된다 ──
    Task<Result<LotDto>> MixingTrackInOutAsync(
        string plantId, string outputLotId, string productId, string equipmentId,
        IReadOnlyList<string> outputRouteSteps, IReadOnlyList<MixingInputDto> inputs,
        string user, CancellationToken ct = default);
}
