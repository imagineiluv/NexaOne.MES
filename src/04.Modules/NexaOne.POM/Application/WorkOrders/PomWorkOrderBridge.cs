using NexaOne.Common;
using NexaOne.POM.Domain;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkOrders;

/// <summary>
/// 호스트의 작업지시 계약을 POM 애플리케이션 서비스에 연결하는 얇은 모듈 Bridge다.
/// HTTP나 영속 규칙을 소유하지 않고 호출 문맥과 도메인 결과의 형식 변환만 담당한다.
/// </summary>
public sealed class PomWorkOrderBridge : IPomWorkOrderBridge
{
    private readonly PomWorkOrderService _service;

    /// <summary>POM 작업지시 애플리케이션 서비스를 사용하는 Bridge를 생성한다.</summary>
    public PomWorkOrderBridge(PomWorkOrderService service) => _service = service;

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> CreateAsync(
        string workOrderId, string productionOrderId, string plantId, string workOrderName,
        string productId, decimal planQty, DateTime? planStartDate, DateTime? planEndDate,
        string? processId, string? equipmentId, string? ownerId, string user,
        string? routingId = null, int? routingStepNo = null, string? workCenterId = null,
        string? areaId = null, string? workOrderType = null, string? salesOrderId = null,
        string? description = null, string? routingScope = null, CancellationToken ct = default)
    {
        PomWorkOrderRoutingScope? parsedScope = null;
        if (!string.IsNullOrWhiteSpace(routingScope))
        {
            if (!Enum.TryParse<PomWorkOrderRoutingScope>(routingScope.Trim(), true, out var value)
                || !Enum.IsDefined(value))
            {
                return Result.Failure<PomWorkOrderDto>(Error.Validation(
                    nameof(routingScope),
                    "Routing scope must be Unbound, Operation, or SerialRoute."));
            }

            parsedScope = value;
        }

        var result = await _service.CreateAsync(new PomWorkOrderCreateCommand(
            workOrderId, productionOrderId, plantId, workOrderName, productId, planQty,
            planStartDate, planEndDate, processId, equipmentId, ownerId, user,
            routingId, routingStepNo, workCenterId, areaId, workOrderType, salesOrderId, description,
            parsedScope), ct);
        return Map(result);
    }

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> ReleaseAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.ReleaseAsync(id, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> StartAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.StartAsync(id, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> ReportAsync(
        string id, decimal goodQty, decimal defectQty, int expectedVersion, string user, string channel, string idempotencyKey,
        string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.ReportAsync(id, goodQty, defectQty, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> HoldAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.HoldAsync(id, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> ReleaseHoldAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.ReleaseHoldAsync(id, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> CompleteAsync(
        string id, decimal goodQty, decimal defectQty, int expectedVersion, string user, string channel, string idempotencyKey,
        string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.CompleteAsync(id, goodQty, defectQty, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <inheritdoc />
    public async Task<Result<PomWorkOrderDto>> CancelAsync(
        string id, int expectedVersion, string user, string channel, string idempotencyKey, string? deviceId, string? remark, CancellationToken ct = default)
        => Map(await _service.CancelAsync(id, Context(user, channel, idempotencyKey, expectedVersion, deviceId, remark), ct));

    /// <summary>단말 메타데이터와 동시성 정보를 서비스용 실행 문맥으로 묶는다.</summary>
    private static PomWorkOrderOperationContext Context(
        string user, string channel, string key, int expectedVersion, string? deviceId, string? remark)
        => new(user, channel, key, expectedVersion, deviceId, remark);

    /// <summary>도메인 성공·실패 의미를 보존하면서 외부 계약 결과로 변환한다.</summary>
    private static Result<PomWorkOrderDto> Map(Result<PomWorkOrder> result)
        => result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<PomWorkOrderDto>(result.Error);

    /// <summary>내부 작업지시 애그리거트를 모듈 경계 밖의 불변 DTO로 투영한다.</summary>
    private static PomWorkOrderDto ToDto(PomWorkOrder w) => new(
        w.Id, w.ProductionOrderId, w.PlantId, w.WorkOrderName, w.ProductId,
        w.PlanQty, w.StartQty, w.CompleteQty, w.ScrapQty, w.Status.ToString(), w.IsHold,
        w.ProcessId, w.EquipmentId, w.OwnerId, w.PlanStartDate, w.PlanEndDate,
        w.StartedAt, w.CompletedAt, w.RoutingId, w.RoutingStepNo, w.WorkCenterId,
        w.AreaId, w.WorkOrderType, w.SalesOrderId, w.Description, w.VersionNo,
        w.RoutingScope.ToString());
}
