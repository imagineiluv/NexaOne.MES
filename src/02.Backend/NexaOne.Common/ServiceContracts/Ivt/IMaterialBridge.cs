using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>
/// 설비 시퀀스, MES 화면과 프로젝트 플러그인이 동일한 자재 소비 원장을 사용하는 작은 계약이다.
/// 호출자는 소비 의미를 해석해 한 건의 명령으로 전달하고, 구현은 멱등성·재고 차감·TRACE 원장·재고 TX를
/// 한 트랜잭션으로 처리한다. 소비 방식별 구현 클래스를 호출자에게 노출하지 않는다.
/// </summary>
public interface IMaterialBridge : INexaModuleBridge
{
    Task<Result<MaterialConsumptionDto>> ConsumeAsync(
        MaterialConsumptionCommand command,
        CancellationToken ct = default);

    Task<Result<MaterialConsumptionDto>> ReverseAsync(
        MaterialConsumptionReversalCommand command,
        CancellationToken ct = default);
}

/// <summary>
/// 자재 소비 쓰기 명령. <paramref name="IdempotencyKey"/>는 설비 재전송과 재시작에도 동일해야 한다.
/// Trace 모드는 SourceEventId가 필수이며, 프로젝트별 TRACE 해석은 이 계약 앞의 플러그인 어댑터가 담당한다.
/// </summary>
public sealed record MaterialConsumptionCommand(
    string ConsumptionId,
    string IdempotencyKey,
    string PlantId,
    string EquipmentId,
    string MaterialLotId,
    string MaterialId,
    decimal Quantity,
    string Unit,
    string Mode,
    DateTime OccurredAt,
    string SourceSystem,
    string SourceEventId,
    string? ProcessLotId = null,
    string? WorkOrderId = null,
    string? ProcessId = null,
    string? RecipeId = null,
    int? RecipeVersion = null,
    string? TraceId = null,
    string? TagId = null,
    string? OperatorId = null,
    string? CorrelationId = null,
    string? MetadataJson = null);

public sealed record MaterialConsumptionReversalCommand(
    string ReversalId,
    string IdempotencyKey,
    string ConsumptionId,
    string Reason,
    DateTime OccurredAt,
    string SourceSystem,
    string? OperatorId = null,
    string? CorrelationId = null);

public sealed record MaterialConsumptionDto(
    string ConsumptionId,
    string IdempotencyKey,
    string PlantId,
    string EquipmentId,
    string MaterialLotId,
    string MaterialId,
    decimal Quantity,
    string Unit,
    string Mode,
    DateTime OccurredAt,
    string OperatorId,
    string SourceSystem,
    string SourceEventId,
    string Status,
    string? ProcessLotId,
    string? WorkOrderId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    string? TraceId,
    string? TagId,
    string? CorrelationId,
    string? ReversalOfId,
    string? MetadataJson);
