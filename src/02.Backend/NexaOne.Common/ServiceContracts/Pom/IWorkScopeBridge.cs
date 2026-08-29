using NexaOne.Common;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// LOT이나 생산 W/O를 전제로 하지 않는 설비 작업의 공통 계약입니다.
/// Batch/Campaign/Carrier/Lot/Equipment/Other를 같은 실행 표면으로 다루되, 대상 식별자와
/// 이력은 별도 애그리거트로 보존합니다. Carrier 작업은 <c>TargetId</c>가 Carrier ID이며
/// 생산 LOT이 생성되지 않습니다.
/// </summary>
public interface IWorkScopeBridge : INexaModuleBridge
{
    Task<Result<IReadOnlyList<WorkScopeDto>>> ListAsync(
        string? plantId = null,
        string? scopeType = null,
        string? targetId = null,
        string? parentScopeId = null,
        string? status = null,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkScopeMemberDto>>> ListMembersAsync(
        string workScopeId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkScopeExecutionDto>>> ListExecutionsAsync(
        string workScopeId,
        CancellationToken ct = default);

    Task<Result<WorkScopeDto>> CreateAsync(
        WorkScopeCreateCommand command,
        CancellationToken ct = default);

    Task<Result<WorkScopeDto>> ExecuteAsync(
        string workScopeId,
        WorkScopeOperationCommand command,
        CancellationToken ct = default);
}

/// <summary>작업이 수행되는 업무 대상의 종류입니다.</summary>
public enum WorkScopeType
{
    Batch,
    Campaign,
    Carrier,
    Lot,
    Equipment,
    Other
}

/// <summary>작업 대상의 상태 전이입니다.</summary>
public enum WorkScopeAction
{
    Release,
    Start,
    Report,
    Hold,
    ReleaseHold,
    Complete,
    Cancel
}

/// <summary>
/// 생산 W/O 부모가 없어도 생성할 수 있는 작업 대상 등록 명령입니다.
/// Campaign은 최상위 그룹, Batch는 Campaign의 하위 그룹, Carrier/Lot/Other는
/// Batch 또는 Campaign의 하위 실행 대상으로 사용할 수 있습니다. Equipment는 설비 자체를
/// 작업 대상으로 삼는 독립 범위이며, `TargetId`와 `EquipmentId`가 같은 설비 식별자를 가리킵니다.
/// WorkOrderId는 기존 생산 W/O와 연결해야 할 때만 사용하는 선택적 상관관계 키이고, 작업 범위의
/// 생성·실행을 생산 W/O에 종속시키지 않습니다. CarrierId는 Batch/Campaign 같은 상위 범위에서
/// 실제 캐리어를 직접 연결할 때 사용하며 Carrier 범위에서는 TargetId에서 자동 결정됩니다.
/// </summary>
public sealed record WorkScopeCreateCommand(
    string WorkScopeId,
    string PlantId,
    WorkScopeType ScopeType,
    string TargetId,
    string Name,
    string? ParentScopeId = null,
    string? EquipmentId = null,
    string? ProductId = null,
    string? ProcessId = null,
    string? RecipeId = null,
    int? RecipeVersion = null,
    decimal? PlanQty = null,
    string? OwnerId = null,
    string? Description = null,
    string? ActorId = null,
    string? WorkOrderId = null,
    string? CarrierId = null,
    string? IdempotencyKey = null);

/// <summary>
/// 작업 대상 상태 전이 명령입니다. 수량은 증분이 아닌 현재 절대 누계이며,
/// 모든 변경은 ExpectedVersion과 IdempotencyKey를 함께 요구합니다.
/// </summary>
public sealed record WorkScopeOperationCommand(
    WorkScopeAction Action,
    string IdempotencyKey,
    int ExpectedVersion,
    decimal? GoodQty = null,
    decimal? DefectQty = null,
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? Remark = null,
    string? ActorId = null,
    string? CarrierId = null,
    string? ResultCode = null,
    string? ResultMetadataJson = null);

/// <summary>작업 대상과 현재 실행 누계를 화면/MES 간에 전달하는 불변 계약입니다.</summary>
public sealed record WorkScopeDto(
    string WorkScopeId,
    string PlantId,
    string ScopeType,
    string TargetId,
    string Name,
    string? ParentScopeId,
    string? EquipmentId,
    string? ProductId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    decimal? PlanQty,
    decimal StartQty,
    decimal CompleteQty,
    decimal ScrapQty,
    string? OwnerId,
    string Status,
    bool IsHold,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Description,
    int VersionNo,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    string? WorkOrderId = null,
    string? CarrierId = null);

/// <summary>Campaign/Batch 범위에 자동 편성된 하위 작업 대상의 읽기 계약입니다.</summary>
public sealed record WorkScopeMemberDto(
    string MemberId,
    string WorkScopeId,
    string MemberScopeId,
    string MemberType,
    string MemberTargetId,
    int SequenceNo,
    DateTime CreatedAt);

/// <summary>작업 대상 상태 전이와 Carrier 세척 결과를 조회하는 실행 이력 계약입니다.</summary>
public sealed record WorkScopeExecutionDto(
    string ExecutionId,
    string WorkScopeId,
    string IdempotencyKey,
    string Action,
    string FromStatus,
    string ToStatus,
    decimal? GoodQty,
    decimal? DefectQty,
    string UserId,
    string? EquipmentId,
    string ClientChannel,
    string? DeviceId,
    DateTime OccurredAt,
    string? Remark,
    int? ExpectedVersion,
    int? ResultVersion,
    string? CarrierId,
    string? ResultCode,
    string? ResultMetadataJson);
