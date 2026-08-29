using NexaOne.Common;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// 실행 가능한 생산 작업지시 전용 모듈 브리지다. 생산관리오더는 계속 <see cref="IPomBridge"/>가
/// 담당하며, 두 계약을 같은 모델이나 저장 경로로 대체하지 않는다.
/// 이 계약의 완료는 작업지시 실적 마감이며, LOT 공정 완료 시 QMS 판정은
/// <see cref="IProductionQualityGateway"/>를 통해 별도로 적용한다.
/// </summary>
public interface IPomWorkOrderBridge : INexaModuleBridge
{
    /// <summary>생산관리오더에 연결된 공정 실행 작업지시를 생성한다.</summary>
    Task<Result<PomWorkOrderDto>> CreateAsync(
        string workOrderId,
        string productionOrderId,
        string plantId,
        string workOrderName,
        string productId,
        decimal planQty,
        DateTime? planStartDate,
        DateTime? planEndDate,
        string? processId,
        string? equipmentId,
        string? ownerId,
        string user,
        string? routingId = null,
        int? routingStepNo = null,
        string? workCenterId = null,
        string? areaId = null,
        string? workOrderType = null,
        string? salesOrderId = null,
        string? description = null,
        string? routingScope = null,
        CancellationToken ct = default);

    /// <summary>작업지시를 Created에서 Released로 전환한다.</summary>
    Task<Result<PomWorkOrderDto>> ReleaseAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default);
    /// <summary>Released 작업지시의 현장 실행을 시작한다.</summary>
    Task<Result<PomWorkOrderDto>> StartAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default);
    /// <summary>양품·불량 절대 누계를 보고한다.</summary>
    Task<Result<PomWorkOrderDto>> ReportAsync(
        string id, decimal goodQty, decimal defectQty, int expectedVersion, string user, string channel, string idempotencyKey,
        string? deviceId, string? remark, CancellationToken ct = default);
    /// <summary>진행 중 작업지시를 보류한다.</summary>
    Task<Result<PomWorkOrderDto>> HoldAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default);
    /// <summary>작업지시 보류를 해제한다.</summary>
    Task<Result<PomWorkOrderDto>> ReleaseHoldAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default);
    /// <summary>최종 실적을 확정하고 작업지시를 완료한다. LOT 공정의 품질 게이트를 대신하지 않는다.</summary>
    Task<Result<PomWorkOrderDto>> CompleteAsync(
        string id, decimal goodQty, decimal defectQty, int expectedVersion, string user, string channel, string idempotencyKey,
        string? deviceId, string? remark, CancellationToken ct = default);
    /// <summary>아직 시작하지 않은 작업지시를 취소한다.</summary>
    Task<Result<PomWorkOrderDto>> CancelAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default);
}
