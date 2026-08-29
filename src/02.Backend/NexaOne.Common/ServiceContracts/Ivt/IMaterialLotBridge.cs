using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>
/// 자재 LOT의 상태·위치·수량 변경을 하나의 원자적 수불 이벤트로 실행한다.
/// 호출자는 작업 종류만 지정하고, 구현은 상태 규칙·멱등성·낙관적 잠금·원장 기록을 숨긴다.
/// </summary>
public interface IMaterialLotBridge : INexaModuleBridge
{
    Task<Result<MaterialLotEventDto>> ExecuteAsync(
        MaterialLotCommand command,
        CancellationToken ct = default);
}

public static class MaterialLotOperations
{
    public const string Receive = "Receive";
    public const string Move = "Move";
    public const string Hold = "Hold";
    public const string Release = "Release";
    public const string Scrap = "Scrap";
    public const string Adjustment = "Adjustment";
}

/// <param name="ExpectedVersion">Receive는 0, 기존 LOT 변경은 현재 VERSION_NO를 전달한다.</param>
/// <param name="Quantity">Receive/Scrap은 양수, Adjustment는 증감 부호를 포함한다.</param>
/// <param name="Location">Receive의 입고 위치 또는 Move의 목적 위치다.</param>
public sealed record MaterialLotCommand(
    string TransactionId,
    string IdempotencyKey,
    string Operation,
    string MaterialLotId,
    int ExpectedVersion,
    DateTime OccurredAt,
    string SourceSystem,
    string SourceEventId,
    string? MaterialId = null,
    string? LotNumber = null,
    decimal? Quantity = null,
    string? Unit = null,
    string? Location = null,
    string? Reason = null,
    DateTime? ExpiryAt = null,
    string? ActorId = null,
    string? CorrelationId = null,
    string? MetadataJson = null);

public sealed record MaterialLotEventDto(
    string TransactionId,
    string IdempotencyKey,
    string Operation,
    string MaterialLotId,
    string MaterialId,
    decimal Quantity,
    decimal BalanceBefore,
    decimal BalanceAfter,
    decimal BalanceDelta,
    string? FromLocation,
    string? ToLocation,
    string ResultStatus,
    int ExpectedVersion,
    int ResultVersion,
    DateTime OccurredAt,
    string ActorId,
    string SourceSystem,
    string SourceEventId,
    string? CorrelationId,
    bool IsReplay);
