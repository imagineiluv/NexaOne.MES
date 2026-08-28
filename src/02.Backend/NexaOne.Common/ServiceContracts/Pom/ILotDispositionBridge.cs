using NexaOne.Common;
using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// Track-Out 불량 관찰을 폐기·재작업·반송·특채·보류 중 하나로 처분하는 공통 업무 계약입니다.
/// 설비별 불량 판정 방식은 플러그인이 소유하고, 이 계약은 확정된 처분과 로그인 작업자 증거만 기록합니다.
/// </summary>
public interface ILotDispositionBridge : INexaModuleBridge
{
    Task<Result<LotDispositionDto>> RecordAsync(
        RecordLotDispositionDto command,
        string actorId,
        CancellationToken ct = default);
}

public sealed record RecordLotDispositionDto(
    string PlantId,
    string LotId,
    string? WorkOrderId,
    string? ProcessId,
    string? DefectExecutionId,
    string? DefectCode,
    string DispositionType,
    decimal Quantity,
    string? ReasonCode,
    string Reason,
    string IdempotencyKey,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? SourceExecutionId = null);

public sealed record LotDispositionDto(
    string DispositionId,
    string PlantId,
    string LotId,
    string? WorkOrderId,
    string? ProcessId,
    string? DefectExecutionId,
    string? DefectCode,
    string DispositionType,
    decimal Quantity,
    string? ReasonCode,
    string Reason,
    string DecidedBy,
    DateTime DecidedAt,
    string? SourceExecutionId,
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId);
