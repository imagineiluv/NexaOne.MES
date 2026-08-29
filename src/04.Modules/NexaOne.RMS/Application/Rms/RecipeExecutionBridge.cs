using NexaOne.Common;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.RMS.Application.Rms;

public sealed class RecipeExecutionBridge : IRecipeExecutionBridge
{
    private readonly RecipeExecutionService _service;

    public RecipeExecutionBridge(RecipeExecutionService service) => _service = service;

    public async Task<Result<RecipeAssignmentDto>> AssignAsync(
        RecipeAssignmentCommand command, CancellationToken ct = default)
    {
        var result = await _service.AssignAsync(command, command.ActorId, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<RecipeAssignmentDto>(result.Error);
    }

    public async Task<IReadOnlyList<RecipeAssignmentDto>> GetAssignmentsAsync(
        string? equipmentId = null,
        string? equipmentClassId = null,
        bool activeOnly = true,
        CancellationToken ct = default)
        => (await _service.GetAssignmentsAsync(equipmentId, equipmentClassId, activeOnly, ct))
            .Select(ToDto).ToList();

    public async Task<Result<RecipeExecutionSnapshotDto>> RecordExecutionAsync(
        RecipeExecutionCommand command, CancellationToken ct = default)
    {
        var result = await _service.RecordExecutionAsync(command, command.ActorId, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<RecipeExecutionSnapshotDto>(result.Error);
    }

    public async Task<Result<RecipeExecutionSnapshotDto>> GetExecutionAsync(
        string executionId, CancellationToken ct = default)
    {
        var result = await _service.GetExecutionAsync(executionId, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<RecipeExecutionSnapshotDto>(result.Error);
    }

    private static RecipeAssignmentDto ToDto(RecipeEquipmentAssignment value) => new(
        value.AssignmentId, value.EquipmentId, value.EquipmentClassId,
        value.RecipeId, value.RecipeVersion, value.EffectiveFrom,
        value.EffectiveTo, value.AssignedBy, value.IsActive);

    private static RecipeExecutionSnapshotDto ToDto(RecipeExecutionSnapshot value) => new(
        value.ExecutionId, value.IdempotencyKey, value.PlantId, value.EquipmentId,
        value.ProcessLotId, value.WorkOrderId, value.ProcessId, value.RecipeId,
        value.RecipeVersion, value.RecipeSnapshotJson, value.ParameterSnapshotJson,
        value.ConditionSnapshotJson, value.AppliedBy, value.AppliedAt, value.Source,
        value.TraceId, value.IsReplay, value.WorkScopeId, value.CarrierId);
}
