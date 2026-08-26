using NexaOne.Common;

namespace NexaOne.ServiceContracts.Rms;

/// <summary>
/// 설비/설비등급의 레시피 선택과 실제 실행 시점의 불변 증거를 관리한다.
/// PLC 주소·단위 변환·다운로드 순서는 프로젝트 플러그인의 책임이며 이 계약에 포함하지 않는다.
/// </summary>
[NexaModuleBridge("Rms", "rmsRecipeExecutionBridge")]
public interface IRecipeExecutionBridge : INexaModuleBridge
{
    Task<Result<RecipeAssignmentDto>> AssignAsync(
        RecipeAssignmentCommand command, CancellationToken ct = default);

    Task<IReadOnlyList<RecipeAssignmentDto>> GetAssignmentsAsync(
        string? equipmentId = null,
        string? equipmentClassId = null,
        bool activeOnly = true,
        CancellationToken ct = default);

    Task<Result<RecipeExecutionSnapshotDto>> RecordExecutionAsync(
        RecipeExecutionCommand command, CancellationToken ct = default);

    Task<Result<RecipeExecutionSnapshotDto>> GetExecutionAsync(
        string executionId, CancellationToken ct = default);
}

public sealed record RecipeAssignmentCommand(
    string AssignmentId,
    string? EquipmentId,
    string? EquipmentClassId,
    string RecipeId,
    int RecipeVersion,
    DateTime? EffectiveFrom = null,
    string? ActorId = null);

public sealed record RecipeAssignmentDto(
    string AssignmentId,
    string? EquipmentId,
    string? EquipmentClassId,
    string RecipeId,
    int RecipeVersion,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string AssignedBy,
    bool IsActive);

public sealed record RecipeExecutionCommand(
    string ExecutionId,
    string IdempotencyKey,
    string PlantId,
    string EquipmentId,
    string RecipeId,
    int RecipeVersion,
    DateTime AppliedAt,
    string Source,
    string? ProcessLotId = null,
    string? WorkOrderId = null,
    string? ProcessId = null,
    string? TraceId = null,
    string? ConditionSnapshotJson = null,
    string? ActorId = null);

public sealed record RecipeExecutionSnapshotDto(
    string ExecutionId,
    string IdempotencyKey,
    string PlantId,
    string EquipmentId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? ProcessId,
    string RecipeId,
    int RecipeVersion,
    string RecipeSnapshotJson,
    string ParameterSnapshotJson,
    string? ConditionSnapshotJson,
    string AppliedBy,
    DateTime AppliedAt,
    string Source,
    string? TraceId,
    bool IsReplay);
