namespace NexaOne.RMS.Application.Rms;

public interface IRecipeExecutionRepository
{
    Task<bool> TrySaveReleasedAssignmentAsync(
        RecipeEquipmentAssignment assignment,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecipeEquipmentAssignment>> GetAssignmentsAsync(
        string? equipmentId,
        string? equipmentClassId,
        bool activeOnly,
        CancellationToken ct = default);

    Task<RecipeExecutionSnapshot?> GetExecutionAsync(
        string executionId, CancellationToken ct = default);

    Task<RecipeExecutionSnapshot?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);

    Task<bool> TryAddExecutionAsync(
        RecipeExecutionSnapshot snapshot, CancellationToken ct = default);
}

public sealed record RecipeEquipmentAssignment(
    string AssignmentId,
    string? EquipmentId,
    string? EquipmentClassId,
    string RecipeId,
    int RecipeVersion,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string AssignedBy,
    bool IsActive);

public sealed record RecipeExecutionSnapshot(
    string ExecutionId,
    string IdempotencyKey,
    string RequestHash,
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
    DateTime CreatedAt,
    bool IsReplay = false);
