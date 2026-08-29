using System.Globalization;
using System.Text.Json;
using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.RMS.Domain;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.RMS.Application.Rms;

/// <summary>
/// 레시피 assignment와 실행 스냅샷의 공통 불변식을 숨긴다. 실제 PLC 적용은 프로젝트 플러그인이 담당한다.
/// </summary>
public sealed class RecipeExecutionService
{
    private const int IdLength = 50;
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private readonly IRecipeRepository _recipes;
    private readonly IRecipeParamRepository _parameters;
    private readonly IRecipeExecutionRepository _executions;
    private readonly IEquipmentDirectory _equipmentDirectory;

    public RecipeExecutionService(
        IRecipeRepository recipes,
        IRecipeParamRepository parameters,
        IRecipeExecutionRepository executions,
        IEquipmentDirectory equipmentDirectory)
    {
        _recipes = recipes;
        _parameters = parameters;
        _executions = executions;
        _equipmentDirectory = equipmentDirectory
                              ?? throw new ArgumentNullException(nameof(equipmentDirectory));
    }

    public async Task<Result<RecipeEquipmentAssignment>> AssignAsync(
        RecipeAssignmentCommand command,
        string? actorId = null,
        CancellationToken ct = default)
    {
        var error = ValidateAssignment(command);
        if (error is not null)
            return Result.Failure<RecipeEquipmentAssignment>(error);
        var actor = CommandActor.Resolve(Text(actorId) ?? Text(command.ActorId));
        if (actor.IsFailure)
            return Result.Failure<RecipeEquipmentAssignment>(actor.Error);

        var recipe = await _recipes.GetByIdAsync(command.RecipeId.Trim(), ct);
        if (recipe is null)
            return Result.Failure<RecipeEquipmentAssignment>(
                Error.NotFoundOf(nameof(Recipe), command.RecipeId));
        if (recipe.Version != command.RecipeVersion)
            return Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                $"Recipe '{recipe.Id}' is version {recipe.Version}, not {command.RecipeVersion}."));
        if (recipe.ApprovalState != RecipeApprovalState.Released)
            return Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                "Only a Released recipe can be assigned to equipment."));

        var equipmentClassId = Text(command.EquipmentClassId);
        if (equipmentClassId is not null
            && !recipe.EquipmentClassId.Equals(equipmentClassId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                $"Recipe '{recipe.Id}' belongs to equipment class '{recipe.EquipmentClassId}', not '{equipmentClassId}'."));

        var now = DateTime.UtcNow;
        var effectiveFrom = Utc(command.EffectiveFrom ?? now);
        if (effectiveFrom > now)
            return Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                "Future-effective recipe assignments are not supported. Activate the assignment when it becomes current."));

        var equipmentId = Text(command.EquipmentId);
        if (equipmentId is not null)
        {
            var equipment = await _equipmentDirectory.GetEquipmentAsync(equipmentId, ct);
            if (equipment is null)
                return Result.Failure<RecipeEquipmentAssignment>(Error.NotFoundOf("Equipment", equipmentId));
            if (!equipment.IsValid)
                return Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                    "RMS.RecipeAssignment.EquipmentInactive",
                    $"Equipment '{equipmentId}' is not active."));
            if (!recipe.EquipmentClassId.Equals(
                    equipment.EquipmentClassId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                    "RMS.RecipeAssignment.EquipmentClassMismatch",
                    $"Recipe '{recipe.Id}' belongs to equipment class '{recipe.EquipmentClassId}', not "
                    + $"'{equipment.EquipmentClassId}'."));
        }

        var assignment = new RecipeEquipmentAssignment(
            command.AssignmentId.Trim(),
            equipmentId,
            equipmentClassId,
            recipe.Id,
            recipe.Version,
            effectiveFrom,
            null,
            actor.Value,
            true);
        return await _executions.TrySaveReleasedAssignmentAsync(assignment, ct)
            ? Result.Success(assignment)
            : Result.Failure<RecipeEquipmentAssignment>(Error.Conflict(
                "Recipe was not Released at the guarded assignment write, or it changed concurrently."));
    }

    public Task<IReadOnlyList<RecipeEquipmentAssignment>> GetAssignmentsAsync(
        string? equipmentId,
        string? equipmentClassId,
        bool activeOnly = true,
        CancellationToken ct = default)
        => _executions.GetAssignmentsAsync(Text(equipmentId), Text(equipmentClassId), activeOnly, ct);

    public async Task<Result<RecipeExecutionSnapshot>> RecordExecutionAsync(
        RecipeExecutionCommand command,
        string? actorId = null,
        CancellationToken ct = default)
    {
        var error = ValidateExecution(command);
        if (error is not null)
            return Result.Failure<RecipeExecutionSnapshot>(error);

        var actorResult = CommandActor.Resolve(Text(actorId) ?? Text(command.ActorId));
        if (actorResult.IsFailure)
            return Result.Failure<RecipeExecutionSnapshot>(actorResult.Error);
        var actor = actorResult.Value;
        var normalized = command with
        {
            ExecutionId = command.ExecutionId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            PlantId = command.PlantId.Trim(),
            EquipmentId = command.EquipmentId.Trim(),
            RecipeId = command.RecipeId.Trim(),
            Source = command.Source.Trim(),
            ProcessLotId = Text(command.ProcessLotId),
            WorkOrderId = Text(command.WorkOrderId),
            ProcessId = Text(command.ProcessId),
            TraceId = Text(command.TraceId),
            ConditionSnapshotJson = Text(command.ConditionSnapshotJson),
            WorkScopeId = Text(command.WorkScopeId),
            CarrierId = Text(command.CarrierId),
            AppliedAt = Utc(command.AppliedAt),
            ActorId = actor,
        };
        var requestHash = Hash(
            normalized.ExecutionId,
            normalized.PlantId,
            normalized.EquipmentId,
            normalized.ProcessLotId ?? string.Empty,
            normalized.WorkOrderId ?? string.Empty,
            normalized.ProcessId ?? string.Empty,
            normalized.WorkScopeId ?? string.Empty,
            normalized.CarrierId ?? string.Empty,
            normalized.RecipeId,
            normalized.RecipeVersion.ToString(CultureInfo.InvariantCulture),
            normalized.ConditionSnapshotJson ?? string.Empty,
            normalized.AppliedAt.ToString("O", CultureInfo.InvariantCulture),
            normalized.Source,
            normalized.TraceId ?? string.Empty,
            actor);

        var replay = await _executions.GetExecutionByIdempotencyKeyAsync(
            normalized.IdempotencyKey, ct);
        if (replay is not null)
            return Replay(replay, requestHash);

        var equipment = await _equipmentDirectory.GetEquipmentAsync(normalized.EquipmentId, ct);
        if (equipment is null)
            return Result.Failure<RecipeExecutionSnapshot>(
                Error.NotFoundOf("Equipment", normalized.EquipmentId));
        if (!equipment.IsValid)
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "RMS.RecipeExecution.EquipmentInactive",
                $"Equipment '{normalized.EquipmentId}' is not active."));
        if (!equipment.PlantId.Equals(normalized.PlantId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "RMS.RecipeExecution.PlantMismatch",
                $"Equipment '{normalized.EquipmentId}' belongs to plant '{equipment.PlantId}', not '{normalized.PlantId}'."));

        var recipe = await _recipes.GetByIdAsync(normalized.RecipeId, ct);
        if (recipe is null)
            return Result.Failure<RecipeExecutionSnapshot>(
                Error.NotFoundOf(nameof(Recipe), normalized.RecipeId));
        if (recipe.ApprovalState != RecipeApprovalState.Released)
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "Only a Released recipe can create an execution snapshot."));
        if (recipe.Version != normalized.RecipeVersion)
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                $"Recipe '{recipe.Id}' is version {recipe.Version}, not {normalized.RecipeVersion}."));
        if (!recipe.EquipmentClassId.Equals(
                equipment.EquipmentClassId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "RMS.RecipeExecution.EquipmentClassMismatch",
                $"Recipe '{recipe.Id}' belongs to equipment class '{recipe.EquipmentClassId}', not "
                + $"'{equipment.EquipmentClassId}'."));

        var assignment = await _executions.GetEffectiveAssignmentAsync(
            normalized.EquipmentId,
            equipment.EquipmentClassId,
            normalized.AppliedAt,
            ct);
        if (assignment is null)
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "RMS.RecipeExecution.AssignmentRequired",
                $"Equipment '{normalized.EquipmentId}' has no effective recipe assignment at {normalized.AppliedAt:O}."));
        if (!assignment.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase)
            || assignment.RecipeVersion != recipe.Version)
            return Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "RMS.RecipeExecution.AssignmentMismatch",
                $"Effective assignment '{assignment.AssignmentId}' selects recipe "
                + $"'{assignment.RecipeId}' version {assignment.RecipeVersion}, not "
                + $"'{recipe.Id}' version {recipe.Version}."));

        var parameters = await _parameters.GetByRecipeAsync(recipe.Id, ct);
        var recipeJson = JsonSerializer.Serialize(new
        {
            recipeId = recipe.Id,
            recipeName = recipe.RecipeName,
            description = recipe.Description,
            equipmentClassId = recipe.EquipmentClassId,
            version = recipe.Version,
            approvalState = recipe.ApprovalState.ToString(),
            releasedAt = recipe.ReleasedAt,
            assignmentId = assignment.AssignmentId,
        }, SnapshotJson);
        var parameterJson = JsonSerializer.Serialize(parameters
            .OrderBy(parameter => parameter.SortOrder)
            .ThenBy(parameter => parameter.ParamName, StringComparer.Ordinal)
            .ThenBy(parameter => parameter.Id, StringComparer.Ordinal)
            .Select(parameter => new
            {
                paramId = parameter.Id,
                paramName = parameter.ParamName,
                paramValue = parameter.ParamValue,
                unit = parameter.Unit,
                sortOrder = parameter.SortOrder,
            }), SnapshotJson);

        var snapshot = new RecipeExecutionSnapshot(
            normalized.ExecutionId,
            normalized.IdempotencyKey,
            requestHash,
            normalized.PlantId,
            normalized.EquipmentId,
            normalized.ProcessLotId,
            normalized.WorkOrderId,
            normalized.ProcessId,
            recipe.Id,
            recipe.Version,
            recipeJson,
            parameterJson,
            normalized.ConditionSnapshotJson,
            actor,
            normalized.AppliedAt,
            normalized.Source,
            normalized.TraceId,
            DateTime.UtcNow,
            false,
            normalized.WorkScopeId,
            normalized.CarrierId);

        if (await _executions.TryAddAssignedExecutionAsync(
                snapshot, assignment.AssignmentId, equipment.EquipmentClassId, ct))
            return Result.Success(snapshot);

        var winner = await _executions.GetExecutionByIdempotencyKeyAsync(
            normalized.IdempotencyKey, ct);
        return winner is not null
            ? Replay(winner, requestHash)
            : Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                "Recipe or its effective equipment assignment changed during the guarded execution write."));
    }

    public async Task<Result<RecipeExecutionSnapshot>> GetExecutionAsync(
        string executionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(executionId))
            return Result.Failure<RecipeExecutionSnapshot>(
                Error.Validation(nameof(executionId), "ExecutionId is required."));
        var snapshot = await _executions.GetExecutionAsync(executionId.Trim(), ct);
        return snapshot is null
            ? Result.Failure<RecipeExecutionSnapshot>(
                Error.NotFoundOf(nameof(RecipeExecutionSnapshot), executionId))
            : Result.Success(snapshot);
    }

    private static Result<RecipeExecutionSnapshot> Replay(
        RecipeExecutionSnapshot existing, string requestHash)
        => string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(existing with { IsReplay = true })
            : Result.Failure<RecipeExecutionSnapshot>(Error.Conflict(
                $"Idempotency key '{existing.IdempotencyKey}' is already used for a different recipe execution."));

    private static Error? ValidateAssignment(RecipeAssignmentCommand command)
    {
        if (command is null) return Error.Validation(nameof(command), "Command is required.");
        if (!ValidId(command.AssignmentId))
            return Error.Validation(nameof(command.AssignmentId), "AssignmentId is required and cannot exceed 50 characters.");
        if (!ValidId(command.RecipeId))
            return Error.Validation(nameof(command.RecipeId), "RecipeId is required and cannot exceed 50 characters.");
        if (command.RecipeVersion <= 0)
            return Error.Validation(nameof(command.RecipeVersion), "RecipeVersion must be greater than zero.");
        var hasEquipment = !string.IsNullOrWhiteSpace(command.EquipmentId);
        var hasClass = !string.IsNullOrWhiteSpace(command.EquipmentClassId);
        if (hasEquipment == hasClass)
            return Error.Validation("AssignmentTarget", "Exactly one of EquipmentId or EquipmentClassId is required.");
        if (hasEquipment && !ValidId(command.EquipmentId))
            return Error.Validation(nameof(command.EquipmentId), "EquipmentId cannot exceed 50 characters.");
        if (hasClass && !ValidId(command.EquipmentClassId))
            return Error.Validation(nameof(command.EquipmentClassId), "EquipmentClassId cannot exceed 50 characters.");
        return null;
    }

    private static Error? ValidateExecution(RecipeExecutionCommand command)
    {
        if (command is null) return Error.Validation(nameof(command), "Command is required.");
        var required = new (string Name, string? Value, int Max)[]
        {
            (nameof(command.ExecutionId), command.ExecutionId, IdLength),
            (nameof(command.IdempotencyKey), command.IdempotencyKey, 100),
            (nameof(command.PlantId), command.PlantId, IdLength),
            (nameof(command.EquipmentId), command.EquipmentId, IdLength),
            (nameof(command.RecipeId), command.RecipeId, IdLength),
            (nameof(command.Source), command.Source, 20),
        };
        foreach (var (name, value, max) in required)
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
                return Error.Validation(name, $"{name} is required and cannot exceed {max} characters.");
        if (command.RecipeVersion <= 0)
            return Error.Validation(nameof(command.RecipeVersion), "RecipeVersion must be greater than zero.");
        if (!OptionalLength(command.ProcessLotId, IdLength)
            || !OptionalLength(command.WorkOrderId, IdLength)
            || !OptionalLength(command.ProcessId, IdLength)
            || !OptionalLength(command.TraceId, 100)
            || !OptionalLength(command.WorkScopeId, IdLength)
            || !OptionalLength(command.CarrierId, 100))
            return Error.Validation("ExecutionContext", "An execution context identifier exceeds its supported length.");
        if (!string.IsNullOrWhiteSpace(command.CarrierId)
            && string.IsNullOrWhiteSpace(command.WorkScopeId))
            return Error.Validation(
                "ExecutionContext",
                "CarrierId requires WorkScopeId so carrier history remains attributable to a work scope.");
        return null;
    }

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= IdLength;
    private static bool OptionalLength(string? value, int max)
        => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= max;
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime Utc(DateTime value) => value == default
        ? DateTime.UtcNow
        : value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    private static string Hash(params object?[] values)
        => CanonicalRequestHash.Compute(values);
}
