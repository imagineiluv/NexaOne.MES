using NexaOne.Common;

namespace NexaOne.ServiceContracts.Est;

/// <summary>
/// LOT 생산과 carrier 같은 non-LOT 설비 출력을 동일한 OEE 실적으로 기록하는 모듈 경계.
/// 설비별 PLC/FDC 의미 해석은 프로젝트 플러그인이 수행하고, EST는 멱등 원장과 집계를 소유한다.
/// </summary>
public interface IEquipmentOutputBridge : INexaModuleBridge
{
    Task<Result<EquipmentOutputDto>> RecordAsync(
        EquipmentOutputCommand command,
        CancellationToken ct = default);
}

public sealed record EquipmentOutputCommand(
    string IdempotencyKey,
    string PlantId,
    string EquipmentId,
    string OutputType,
    decimal TotalQuantity,
    decimal GoodQuantity,
    decimal DefectQuantity,
    string Unit,
    DateTime OccurredAt,
    string Source,
    string? SourceEventId = null,
    string? CarrierId = null,
    string? ProcessLotId = null,
    string? WorkOrderId = null,
    string? ProcessId = null,
    string? RecipeId = null,
    int? RecipeVersion = null,
    string? ActorId = null,
    string? CorrelationId = null,
    string? MetadataJson = null,
    bool IsLotOutput = false);

public sealed record EquipmentOutputDto(
    string OutputEventId,
    string IdempotencyKey,
    string PlantId,
    string EquipmentId,
    string OutputType,
    decimal TotalQuantity,
    decimal GoodQuantity,
    decimal DefectQuantity,
    string Unit,
    DateTime OccurredAt,
    string Source,
    string ActorId,
    string? CarrierId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? CorrelationId,
    bool IsLotOutput = false);
