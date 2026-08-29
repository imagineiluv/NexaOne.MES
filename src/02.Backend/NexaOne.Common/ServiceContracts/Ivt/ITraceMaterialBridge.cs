using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>
/// TRACE 자재 투영에 필요한 두 런타임 경계만 노출합니다. binding은 정지된 maintenance에서,
/// feed session은 실제 자재 장착/해제 시점에 호출하며 구현 저장소는 IVT 안에 숨깁니다.
/// </summary>
public interface ITraceMaterialBridge : INexaModuleBridge
{
    Task<Result<TraceBindingDto>> ExecuteBindingAsync(
        TraceBindingCommand command,
        CancellationToken ct = default);

    Task<Result<FeedSessionDto>> ExecuteFeedSessionAsync(
        FeedSessionCommand command,
        CancellationToken ct = default);
}

public static class TraceBindingOperations
{
    public const string Create = "Create";
    public const string Retire = "Retire";
}

/// <summary>
/// TRACE 원천과 자재 투입점을 연결하는 설정 명령입니다. 설정 변경은 온라인 운전 경로가 아니라
/// 명시적으로 quiesce된 maintenance window에서만 허용됩니다.
/// </summary>
public sealed record TraceBindingCommand(
    string Operation,
    string BindingId,
    int ExpectedVersion,
    string IdempotencyKey,
    string SourceSystem,
    string SourceEventId,
    DateTime OccurredAt,
    DateTime EffectiveAt,
    string? PlantId = null,
    string? EquipmentId = null,
    string? ParameterId = null,
    string? FeedPointId = null,
    string? CalculationMode = null,
    decimal? ScaleFactor = null,
    decimal? PulseQuantity = null,
    string? OutputUnit = null,
    string? ActorId = null,
    string? CorrelationId = null,
    string? Reason = null);

public sealed record TraceBindingDto(
    string BindingId,
    string PlantId,
    string EquipmentId,
    string ParameterId,
    string FeedPointId,
    string CalculationMode,
    decimal ScaleFactor,
    decimal? PulseQuantity,
    string OutputUnit,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    bool IsActive,
    int Version,
    string LastOperation,
    string ActorId,
    DateTime OccurredAt,
    string SourceSystem,
    string SourceEventId,
    string? CorrelationId,
    string? Reason,
    bool IsReplay);

public static class FeedSessionOperations
{
    public const string Mount = "Mount";
    public const string Unmount = "Unmount";
}

/// <summary>
/// 설비 투입점에 자재 LOT를 장착하거나 해제하는 물리 작업 명령입니다. 작업자와 원천 이벤트를
/// 함께 고정해 TRACE 소비 이력이 어느 장착 세션에서 발생했는지 재현할 수 있게 합니다.
/// </summary>
public sealed record FeedSessionCommand(
    string Operation,
    string FeedSessionId,
    int ExpectedVersion,
    string IdempotencyKey,
    string SourceSystem,
    string SourceEventId,
    DateTime OccurredAt,
    string? PlantId = null,
    string? EquipmentId = null,
    string? FeedPointId = null,
    string? MaterialLotId = null,
    string? MaterialId = null,
    string? ProcessLotId = null,
    string? WorkOrderId = null,
    string? ProcessId = null,
    string? RecipeId = null,
    int? RecipeVersion = null,
    string? ActorId = null,
    string? CorrelationId = null,
    string? Reason = null);

public sealed record FeedSessionDto(
    string FeedSessionId,
    string PlantId,
    string EquipmentId,
    string FeedPointId,
    string MaterialLotId,
    string MaterialId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    DateTime MountedAt,
    string MountedBy,
    DateTime? UnmountedAt,
    string? UnmountedBy,
    string Status,
    int Version,
    string LastOperation,
    string ActorId,
    DateTime OccurredAt,
    string SourceSystem,
    string SourceEventId,
    string? CorrelationId,
    string? Reason,
    bool IsReplay);
