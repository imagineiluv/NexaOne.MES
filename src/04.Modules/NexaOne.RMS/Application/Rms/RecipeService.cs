using NexaOne.RMS.Domain;
using NexaOne.Common;
using NexaOne.Application.Idempotency;
using NexaOne.ServiceContracts.Rms;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.RMS.Application.Rms;

public class RecipeService
{
    private readonly IRecipeRepository _recipeRepository;
    private readonly IRecipeParamRepository _paramRepository;
    private readonly IApprovalProcess? _approvals;

    public RecipeService(IRecipeRepository recipeRepository, IRecipeParamRepository paramRepository,
        IApprovalProcess? approvals = null)
    {
        _recipeRepository = recipeRepository;
        _paramRepository = paramRepository;
        _approvals = approvals;
    }

    public async Task<Result<IReadOnlyList<Recipe>>> GetByEquipmentClassAsync(
        string equipmentClassId, CancellationToken ct = default)
        => await GetRecipesAsync(equipmentClassId, null, ct);

    public async Task<Result<IReadOnlyList<Recipe>>> GetRecipesAsync(
        string? equipmentClassId,
        RecipeApprovalState? state,
        CancellationToken ct = default)
    {
        var list = await _recipeRepository.GetAsync(equipmentClassId, state, ct);
        return Result.Success(list);
    }

    public async Task<Result<IReadOnlyList<Recipe>>> GetByStateAsync(
        RecipeApprovalState state, CancellationToken ct = default)
        => await GetRecipesAsync(null, state, ct);

    public Task<int> GetCountByStateAsync(RecipeApprovalState state, CancellationToken ct = default)
        => _recipeRepository.GetCountByStateAsync(state, ct);

    public async Task<Result<Recipe>> GetRecipeAsync(string recipeId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        return recipe is null
            ? Result.Failure<Recipe>(Error.NotFoundOf(nameof(Recipe), recipeId))
            : Result.Success(recipe);
    }

    public async Task<Result<Recipe>> CreateRecipeAsync(
        RecipeCreateCommand command, CancellationToken ct = default)
    {
        if (command is null)
            return Result.Failure<Recipe>(Error.Validation(nameof(command), "Recipe create command is required."));
        var contextError = ValidateCommandIdentity(command.ActorId, command.IdempotencyKey);
        if (contextError is not null) return Result.Failure<Recipe>(contextError);

        // 식별자를 포함한 모든 header 값은 domain factory를 우회하지 않는다.
        var created = Recipe.Create(
            command.RecipeId, command.Name, command.Description, command.EquipmentClassId);
        if (created.IsFailure) return created;

        var actor = command.ActorId.Trim();
        var key = command.IdempotencyKey.Trim();
        var recipe = created.Value;
        var requestHash = CanonicalRequestHash.Compute(
            "Create", recipe.Id, recipe.RecipeName, recipe.Description,
            recipe.EquipmentClassId, actor);
        var replay = await _recipeRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null)
            return await ReplayRecipeWriteAsync(replay, requestHash, key, ct);

        var write = new RecipeWriteRecord(
            $"RWC_{Guid.NewGuid():N}", "Create", key, requestHash,
            recipe.Id, null, actor, DateTime.UtcNow);
        if (await _recipeRepository.TryAddAsync(recipe, write, ct))
            return Result.Success(recipe);

        replay = await _recipeRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null)
            return await ReplayRecipeWriteAsync(replay, requestHash, key, ct);
        return Result.Failure<Recipe>(Error.Conflict(
            "RMS.Recipe.CreateConflict",
            $"Recipe '{recipe.Id}' already exists or changed before the create command committed."));
    }

    public async Task<Result> RequestApprovalAsync(
        string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => await TransitionAsync(
            recipeId, "RequestApproval", context, null,
            static (recipe, _, _) => recipe.RequestApproval(), ct);

    public async Task<Result> Approve1Async(
        string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => await TransitionAsync(
            recipeId, "Approve1", context, null,
            static (recipe, actor, _) => recipe.Approve1(actor), ct);

    public async Task<Result> Approve2Async(
        string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => await TransitionAsync(
            recipeId, "Approve2", context, null,
            static (recipe, actor, _) => recipe.Approve2(actor), ct);

    public async Task<Result> ReleaseAsync(
        string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => await TransitionAsync(
            recipeId, "Release", context, null,
            static (recipe, actor, _) => recipe.Release(actor), ct);

    public async Task<Result> RejectAsync(
        string recipeId, string reason, RecipeCommandContext context, CancellationToken ct = default)
        => await TransitionAsync(
            recipeId, "Reject", context, reason,
            static (recipe, _, normalizedReason) => recipe.Reject(normalizedReason ?? string.Empty), ct);

    public Task<IReadOnlyList<RecipeApprovalHistoryRecord>> GetApprovalHistoryAsync(
        string recipeId, CancellationToken ct = default)
        => _recipeRepository.GetApprovalHistoryAsync(recipeId, ct);

    private async Task<Result> TransitionAsync(
        string recipeId,
        string action,
        RecipeCommandContext context,
        string? reason,
        Func<Recipe, string, string?, Result> mutation,
        CancellationToken ct)
    {
        var normalizedId = recipeId?.Trim() ?? string.Empty;
        var validation = ValidateCommandContext(normalizedId, context, reason);
        if (validation is not null) return Result.Failure(validation);

        var actor = context.ActorId.Trim();
        var key = context.IdempotencyKey.Trim();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var requestHash = CanonicalRequestHash.Compute(
            normalizedId, action, actor, normalizedReason);
        var replay = await _recipeRepository.GetApprovalHistoryByIdempotencyKeyAsync(key, ct);
        if (replay is not null)
        {
            if (_approvals is not null && action is ("RequestApproval" or "Approve2" or "Reject")
                && await _approvals.GetCurrentAsync("Recipe", normalizedId, ct) is null)
                return Result.Failure(ApprovalMissing(normalizedId));
            return ReplayTransition(replay, requestHash, key);
        }

        var recipe = await _recipeRepository.GetByIdAsync(normalizedId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), normalizedId));

        var expectedState = recipe.ApprovalState;
        var changed = mutation(recipe, actor, normalizedReason);
        if (changed.IsFailure) return changed;

        var transition = new RecipeTransitionWrite(key, requestHash, actor, normalizedReason);
        if (_approvals is not null)
            return await TransitionWithSharedApprovalAsync(
                recipe, expectedState, action, transition, ct);
        if (await _recipeRepository.TryTransitionAsync(recipe, expectedState, transition, ct))
            return Result.Success();

        replay = await _recipeRepository.GetApprovalHistoryByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayTransition(replay, requestHash, key);
        return Result.Failure(Error.Conflict(
            "RMS.Recipe.ConcurrentTransition",
            $"Recipe '{recipe.Id}' changed from '{expectedState}' before this transition could be committed."));
    }

    private async Task<Result> TransitionWithSharedApprovalAsync(
        Recipe recipe, RecipeApprovalState expectedState, string action,
        RecipeTransitionWrite transition, CancellationToken ct)
    {
        var approvals = _approvals!;
        var actor = transition.ActorId;
        var key = transition.IdempotencyKey;
        var hash = transition.RequestHash;
        // 공통 승인 상태기에는 승인 완료 후 반려 전이가 없으므로 Pending 단계에서만 반려한다.
        if (action == "Reject" && expectedState is not
            (RecipeApprovalState.WaitApproval or RecipeApprovalState.Approved1))
            return Result.Failure(Error.Conflict(
                "RMS.Recipe.RejectState", "Only a recipe awaiting approval can be rejected."));
        var approval = action == "RequestApproval"
            ? null
            : await approvals.GetCurrentAsync("Recipe", recipe.Id, ct);
        if (action != "RequestApproval" && approval is null)
            return Result.Failure(ApprovalMissing(recipe.Id));

        if (action == "Approve1")
        {
            if (approval!.Status != "Pending")
                return Result.Failure(ApprovalStateConflict(recipe.Id, approval.Status));
            if (string.Equals(approval.RequestedBy.Trim(), actor, StringComparison.OrdinalIgnoreCase))
                return Result.Failure(Error.Conflict(
                    "RMS.Recipe.SelfApproval", "The requester cannot approve their own recipe."));
        }
        else if (action == "Release" && approval!.Status != "Approved")
            return Result.Failure(ApprovalStateConflict(recipe.Id, approval.Status));

        if (action == "Release")
            return await PersistLocalTransitionAsync(recipe, expectedState, transition, ct);

        async Task PersistDocument(System.Data.Common.DbTransaction transaction, CancellationToken token)
        {
            if (!await _recipeRepository.TryTransitionInTransactionAsync(
                    recipe, expectedState, transition, transaction, token))
                throw new InvalidOperationException(
                    $"RMS_RECIPE_DOCUMENT_CONFLICT: recipe '{recipe.Id}' changed concurrently.");
        }

        try
        {
            if (action == "RequestApproval")
                await approvals.SubmitWithDocumentAsync(
                    new ApprovalRequest("Recipe", recipe.Id, recipe.RecipeName),
                    actor, key, hash, PersistDocument, ct);
            else if (action == "Approve1")
                await approvals.WritePendingDocumentAsync(
                    approval!.ApprovalId, actor, PersistDocument, ct);
            else
            {
                if (approval!.Status != "Pending")
                    return Result.Failure(ApprovalStateConflict(recipe.Id, approval.Status));
                await approvals.DecideWithDocumentAsync(
                    new ApprovalDecision(approval.ApprovalId, action == "Approve2", transition.Reason),
                    actor, key, hash, PersistDocument, ct);
            }
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("RMS_RECIPE_DOCUMENT_CONFLICT:", StringComparison.Ordinal))
        {
            var replay = await _recipeRepository.GetApprovalHistoryByIdempotencyKeyAsync(key, ct);
            if (replay is not null) return ReplayTransition(replay, hash, key);
            return Result.Failure(Error.Conflict(
                "RMS.Recipe.ConcurrentTransition", ex.Message));
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("APPROVAL_REQUEST_CONFLICT:", StringComparison.Ordinal))
        {
            return Result.Failure(Error.Conflict("RMS.Recipe.ApprovalConflict", ex.Message));
        }

        var persisted = await _recipeRepository.GetApprovalHistoryByIdempotencyKeyAsync(key, ct);
        return persisted is null
            ? Result.Failure(Error.Conflict(
                "RMS.Recipe.ApprovalInvariant",
                $"Shared approval committed without recipe transition '{key}'."))
            : ReplayTransition(persisted, hash, key);
    }

    private async Task<Result> PersistLocalTransitionAsync(
        Recipe recipe, RecipeApprovalState expectedState,
        RecipeTransitionWrite transition, CancellationToken ct)
    {
        if (await _recipeRepository.TryTransitionAsync(recipe, expectedState, transition, ct))
            return Result.Success();
        var replay = await _recipeRepository.GetApprovalHistoryByIdempotencyKeyAsync(
            transition.IdempotencyKey, ct);
        return replay is not null
            ? ReplayTransition(replay, transition.RequestHash, transition.IdempotencyKey)
            : Result.Failure(Error.Conflict(
                "RMS.Recipe.ConcurrentTransition",
                $"Recipe '{recipe.Id}' changed from '{expectedState}' before this transition could commit."));
    }

    private static Error ApprovalMissing(string recipeId) => Error.Conflict(
        "RMS.Recipe.ApprovalMissing",
        $"Recipe '{recipeId}' has no shared approval request.");

    private static Error ApprovalStateConflict(string recipeId, string status) => Error.Conflict(
        "RMS.Recipe.ApprovalState",
        $"Shared approval for recipe '{recipeId}' is {status}.");

    private static Result ReplayTransition(
        RecipeApprovalHistoryRecord replay, string requestHash, string idempotencyKey)
        => string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                "RMS.Recipe.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}' was already used for a different recipe transition."));

    private static Error? ValidateCommandContext(
        string recipeId, RecipeCommandContext? context, string? reason)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
            return Error.Validation(nameof(recipeId), "Recipe ID is required.");
        if (context is null || string.IsNullOrWhiteSpace(context.ActorId))
            return Error.Validation(nameof(RecipeCommandContext.ActorId), "Actor ID is required.");
        if (context.ActorId.Trim().Length > 50)
            return Error.Validation(nameof(RecipeCommandContext.ActorId), "Actor ID cannot exceed 50 characters.");
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey))
            return Error.Validation(nameof(RecipeCommandContext.IdempotencyKey), "Idempotency key is required.");
        if (context.IdempotencyKey.Trim().Length > 100)
            return Error.Validation(nameof(RecipeCommandContext.IdempotencyKey), "Idempotency key cannot exceed 100 characters.");
        if (reason?.Trim().Length > 500)
            return Error.Validation(nameof(reason), "Reason cannot exceed 500 characters.");
        return null;
    }

    private static Error? ValidateCommandIdentity(string? actorId, string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return Error.Validation("ActorId", "Actor ID is required.");
        if (actorId.Trim().Length > 50)
            return Error.Validation("ActorId", "Actor ID cannot exceed 50 characters.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error.Validation("IdempotencyKey", "Idempotency key is required.");
        if (idempotencyKey.Trim().Length > 100)
            return Error.Validation("IdempotencyKey", "Idempotency key cannot exceed 100 characters.");
        return null;
    }

    private async Task<Result<Recipe>> ReplayRecipeWriteAsync(
        RecipeWriteRecord replay, string requestHash, string idempotencyKey, CancellationToken ct)
    {
        if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
            return Result.Failure<Recipe>(Error.Conflict(
                "RMS.Recipe.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}' was already used for a different recipe write."));

        var recipe = await _recipeRepository.GetByIdAsync(replay.RecipeId, ct);
        return recipe is not null
            ? Result.Success(recipe)
            : Result.Failure<Recipe>(Error.Conflict(
                "RMS.Recipe.ReplayInvariant",
                $"Recipe command '{replay.CommandId}' exists but result recipe '{replay.RecipeId}' is missing."));
    }

    // ── Recipe Params ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RecipeParam>> GetParamsAsync(string recipeId, CancellationToken ct = default)
        => await _paramRepository.GetByRecipeAsync(recipeId, ct);

    public async Task<Result<RecipeParam>> AddParamAsync(
        RecipeParamAddCommand command, CancellationToken ct = default)
    {
        if (command is null)
            return Result.Failure<RecipeParam>(Error.Validation(nameof(command), "Recipe parameter add command is required."));
        var contextError = ValidateCommandIdentity(command.ActorId, command.IdempotencyKey);
        if (contextError is not null) return Result.Failure<RecipeParam>(contextError);

        var created = RecipeParam.Create(
            command.ParamId, command.RecipeId, command.ParamName, command.ParamValue,
            command.Unit, command.SortOrder);
        if (created.IsFailure) return created;
        var param = created.Value;
        var actor = command.ActorId.Trim();
        var key = command.IdempotencyKey.Trim();
        var requestHash = CanonicalRequestHash.Compute(
            param.Id, "Add", param.RecipeId, param.ParamName, param.ParamValue,
            param.Unit, param.SortOrder, actor);
        var replay = await _paramRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayParamAdd(replay, requestHash, key);

        var recipe = await _recipeRepository.GetByIdAsync(param.RecipeId, ct);
        if (recipe is null)
            return Result.Failure<RecipeParam>(Error.NotFoundOf(nameof(Recipe), param.RecipeId));
        if (recipe.ApprovalState != RecipeApprovalState.Draft)
            return Result.Failure<RecipeParam>(Error.Conflict(
                $"Only a Draft recipe can modify parameters. Current state: {recipe.ApprovalState}."));

        var write = new RecipeParamWriteRecord(
            $"RPC_{Guid.NewGuid():N}", "Add", key, requestHash, param.Id, param.RecipeId,
            param.ParamName, param.ParamValue, param.Unit, param.SortOrder,
            null, param.Version, actor, DateTime.UtcNow);
        if (await _paramRepository.TryAddAsync(param, write, ct))
            return Result.Success(param);

        replay = await _paramRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayParamAdd(replay, requestHash, key);
        return Result.Failure<RecipeParam>(Error.Conflict(
            "RMS.RecipeParam.AddConflict",
            $"Parameter '{param.Id}' already exists or its recipe left Draft state."));
    }

    public async Task<Result> UpdateParamAsync(
        RecipeParamUpdateCommand command, CancellationToken ct = default)
    {
        var validation = ValidateParamUpdate(command);
        if (validation is not null) return Result.Failure(validation);

        var paramId = command.ParamId.Trim();
        var key = command.IdempotencyKey.Trim();
        var actor = command.ActorId.Trim();
        var requestHash = CanonicalRequestHash.Compute(
            paramId, "Update", command.NewValue, command.ExpectedVersion, actor);
        var replay = await _paramRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayParamWrite(replay, requestHash, key);

        var param = await _paramRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFoundOf(nameof(RecipeParam), paramId));

        var recipe = await _recipeRepository.GetByIdAsync(param.RecipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), param.RecipeId));
        if (recipe.ApprovalState != RecipeApprovalState.Draft)
            return Result.Failure(Error.Conflict(
                $"Only a Draft recipe can modify parameters. Current state: {recipe.ApprovalState}."));
        if (param.Version != command.ExpectedVersion)
            return Result.Failure(Error.Conflict(
                "RMS.RecipeParam.ConcurrentUpdate",
                $"Recipe parameter '{param.Id}' changed concurrently. Current version: {param.Version}."));

        var update = new RecipeParamWriteRecord(
            $"RPC_{Guid.NewGuid():N}", "Update", key, requestHash,
            param.Id, param.RecipeId, param.ParamName, command.NewValue,
            param.Unit, param.SortOrder, command.ExpectedVersion,
            command.ExpectedVersion + 1, actor, DateTime.UtcNow);
        if (await _paramRepository.TryUpdateAsync(update, ct))
            return Result.Success();

        replay = await _paramRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayParamWrite(replay, requestHash, key);
        var current = await _paramRepository.GetByIdAsync(param.Id, ct);
        return Result.Failure(Error.Conflict(
            "RMS.RecipeParam.ConcurrentUpdate",
            current is null
                ? $"Recipe parameter '{param.Id}' was removed before this update could be committed."
                : $"Recipe parameter '{param.Id}' or its recipe changed before this update could be committed. Current version: {current.Version}."));
    }

    private static Result ReplayParamWrite(
        RecipeParamWriteRecord replay, string requestHash, string idempotencyKey)
        => string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                "RMS.RecipeParam.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}' was already used for a different recipe-parameter write."));

    private static Result<RecipeParam> ReplayParamAdd(
        RecipeParamWriteRecord replay, string requestHash, string idempotencyKey)
    {
        if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
            return Result.Failure<RecipeParam>(Error.Conflict(
                "RMS.RecipeParam.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}' was already used for a different recipe-parameter write."));
        if (!string.Equals(replay.CommandType, "Add", StringComparison.Ordinal)
            || replay.ParamName is null || replay.ParamValue is null
            || replay.Unit is null || replay.SortOrder is null)
            return Result.Failure<RecipeParam>(Error.Conflict(
                "RMS.RecipeParam.ReplayInvariant",
                $"Parameter command '{replay.CommandId}' does not contain an Add result snapshot."));

        return Result.Success(RecipeParam.Restore(
            replay.ParamId, replay.RecipeId, replay.ParamName, replay.ParamValue,
            replay.Unit, replay.SortOrder.Value, replay.ResultVersion));
    }

    private static Error? ValidateParamUpdate(RecipeParamUpdateCommand? command)
    {
        if (command is null)
            return Error.Validation(nameof(command), "Recipe parameter update command is required.");
        if (string.IsNullOrWhiteSpace(command.ParamId))
            return Error.Validation(nameof(command.ParamId), "Parameter ID is required.");
        if (command.NewValue is null)
            return Error.Validation(nameof(command.NewValue), "Parameter value is required.");
        if (command.NewValue.Length > 500)
            return Error.Validation(nameof(command.NewValue), "Parameter value cannot exceed 500 characters.");
        if (command.ExpectedVersion < 1)
            return Error.Validation(nameof(command.ExpectedVersion), "Expected version must be at least 1.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return Error.Validation(nameof(command.IdempotencyKey), "Idempotency key is required.");
        if (command.IdempotencyKey.Trim().Length > 100)
            return Error.Validation(nameof(command.IdempotencyKey), "Idempotency key cannot exceed 100 characters.");
        if (string.IsNullOrWhiteSpace(command.ActorId))
            return Error.Validation(nameof(command.ActorId), "Actor ID is required.");
        if (command.ActorId.Trim().Length > 50)
            return Error.Validation(nameof(command.ActorId), "Actor ID cannot exceed 50 characters.");
        return null;
    }

    public async Task<Result> DeleteParamAsync(
        RecipeParamDeleteCommand command, CancellationToken ct = default)
    {
        var validation = ValidateParamDelete(command);
        if (validation is not null) return Result.Failure(validation);

        var paramId = command.ParamId.Trim();
        var actor = command.ActorId.Trim();
        var key = command.IdempotencyKey.Trim();
        var requestHash = CanonicalRequestHash.Compute(
            paramId, "Delete", command.ExpectedVersion, actor);
        var replay = await _paramRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayParamWrite(replay, requestHash, key);

        var param = await _paramRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFoundOf(nameof(RecipeParam), paramId));

        var recipe = await _recipeRepository.GetByIdAsync(param.RecipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), param.RecipeId));
        if (recipe.ApprovalState != RecipeApprovalState.Draft)
            return Result.Failure(Error.Conflict(
                $"Only a Draft recipe can modify parameters. Current state: {recipe.ApprovalState}."));

        if (param.Version != command.ExpectedVersion)
            return Result.Failure(Error.Conflict(
                "RMS.RecipeParam.ConcurrentDelete",
                $"Recipe parameter '{param.Id}' changed concurrently. Current version: {param.Version}."));

        var write = new RecipeParamWriteRecord(
            $"RPC_{Guid.NewGuid():N}", "Delete", key, requestHash,
            param.Id, param.RecipeId, param.ParamName, param.ParamValue,
            param.Unit, param.SortOrder, command.ExpectedVersion,
            command.ExpectedVersion, actor, DateTime.UtcNow);
        if (await _paramRepository.TryDeleteAsync(write, ct))
            return Result.Success();

        replay = await _paramRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null) return ReplayParamWrite(replay, requestHash, key);
        var current = await _paramRepository.GetByIdAsync(param.Id, ct);
        return Result.Failure(Error.Conflict(
            "RMS.RecipeParam.ConcurrentDelete",
            current is null
                ? $"Recipe parameter '{param.Id}' was removed by another command."
                : $"Recipe parameter '{param.Id}' or its recipe changed before delete committed. Current version: {current.Version}."));
    }

    public async Task<Result<Recipe>> CreateNewVersionAsync(
        RecipeVersionCreateCommand command, CancellationToken ct = default)
    {
        if (command is null)
            return Result.Failure<Recipe>(Error.Validation(nameof(command), "Recipe version command is required."));
        var contextError = ValidateCommandIdentity(command.ActorId, command.IdempotencyKey);
        if (contextError is not null) return Result.Failure<Recipe>(contextError);
        var sourceRecipeId = command.SourceRecipeId?.Trim() ?? string.Empty;
        var newRecipeId = command.NewRecipeId?.Trim() ?? string.Empty;
        if (sourceRecipeId.Length == 0)
            return Result.Failure<Recipe>(Error.Validation(nameof(command.SourceRecipeId), "Source recipe ID is required."));

        var source = await _recipeRepository.GetByIdAsync(sourceRecipeId, ct);
        if (source is null)
            return Result.Failure<Recipe>(Error.NotFoundOf(nameof(Recipe), sourceRecipeId));
        if (source.ApprovalState != RecipeApprovalState.Released)
            return Result.Failure<Recipe>(Error.Conflict("Only Released recipes can have a new version."));

        var createdVersion = source.CreateNewVersion(newRecipeId);
        if (createdVersion.IsFailure) return createdVersion;
        var newVersion = createdVersion.Value;
        var actor = command.ActorId.Trim();
        var key = command.IdempotencyKey.Trim();
        var requestHash = CanonicalRequestHash.Compute(
            "CreateVersion", source.Id, newVersion.Id, actor);
        var replay = await _recipeRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null)
            return await ReplayRecipeWriteAsync(replay, requestHash, key, ct);

        var sourceParams = await _paramRepository.GetByRecipeAsync(sourceRecipeId, ct);
        var copiedParams = new List<RecipeParam>(sourceParams.Count);
        foreach (var sourceParam in sourceParams)
        {
            var copied = RecipeParam.Create(
                Guid.NewGuid().ToString("N"),
                newRecipeId,
                sourceParam.ParamName,
                sourceParam.ParamValue,
                sourceParam.Unit,
                sourceParam.SortOrder);
            if (copied.IsFailure)
                return Result.Failure<Recipe>(copied.Error);
            copiedParams.Add(copied.Value);
        }

        var write = new RecipeWriteRecord(
            $"RWC_{Guid.NewGuid():N}", "CreateVersion", key, requestHash,
            newVersion.Id, source.Id, actor, DateTime.UtcNow);
        if (await _recipeRepository.TryAddVersionAsync(newVersion, copiedParams, write, ct))
            return Result.Success(newVersion);

        replay = await _recipeRepository.GetWriteByIdempotencyKeyAsync(key, ct);
        if (replay is not null)
            return await ReplayRecipeWriteAsync(replay, requestHash, key, ct);
        return Result.Failure<Recipe>(Error.Conflict(
            "RMS.Recipe.CreateVersionConflict",
            $"Recipe version '{newVersion.Id}' already exists or the source changed before commit."));
    }

    private static Error? ValidateParamDelete(RecipeParamDeleteCommand? command)
    {
        if (command is null)
            return Error.Validation(nameof(command), "Recipe parameter delete command is required.");
        if (string.IsNullOrWhiteSpace(command.ParamId))
            return Error.Validation(nameof(command.ParamId), "Parameter ID is required.");
        if (command.ExpectedVersion < 1)
            return Error.Validation(nameof(command.ExpectedVersion), "Expected version must be at least 1.");
        return ValidateCommandIdentity(command.ActorId, command.IdempotencyKey);
    }
}
