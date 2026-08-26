using NexaOne.RMS.Domain;
using NexaOne.Common;

namespace NexaOne.RMS.Application.Rms;

public class RecipeService
{
    private readonly IRecipeRepository _recipeRepository;
    private readonly IRecipeParamRepository _paramRepository;

    public RecipeService(IRecipeRepository recipeRepository, IRecipeParamRepository paramRepository)
    {
        _recipeRepository = recipeRepository;
        _paramRepository = paramRepository;
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
        string recipeId,
        string name,
        string desc,
        string equipmentClassId,
        CancellationToken ct = default)
    {
        var result = Recipe.Create(recipeId, name, desc, equipmentClassId);
        if (result.IsFailure) return result;

        await _recipeRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> RequestApprovalAsync(string recipeId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), recipeId));

        var result = recipe.RequestApproval();
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), recipeId));

        var result = recipe.Approve1(approverId);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), recipeId));

        var result = recipe.Approve2(approverId);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), recipeId));

        var result = recipe.Release(releaserId);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), recipeId));

        var result = recipe.Reject(reason);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    // ── Recipe Params ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RecipeParam>> GetParamsAsync(string recipeId, CancellationToken ct = default)
        => await _paramRepository.GetByRecipeAsync(recipeId, ct);

    public async Task<Result<RecipeParam>> AddParamAsync(
        string paramId, string recipeId, string paramName, string paramValue,
        string unit, int sortOrder, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure<RecipeParam>(Error.NotFoundOf(nameof(Recipe), recipeId));
        if (recipe.ApprovalState == RecipeApprovalState.Released)
            return Result.Failure<RecipeParam>(Error.Conflict("Cannot modify params of a Released recipe."));

        var result = RecipeParam.Create(paramId, recipeId, paramName, paramValue, unit, sortOrder);
        if (result.IsFailure) return result;
        var added = await _paramRepository.TryAddIfRecipeEditableAsync(result.Value, ct);
        return added
            ? result
            : Result.Failure<RecipeParam>(Error.Conflict(
                "Recipe was released while its parameter was being added."));
    }

    public async Task<Result> UpdateParamAsync(
        string paramId, string newValue, CancellationToken ct = default)
    {
        var param = await _paramRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFoundOf(nameof(RecipeParam), paramId));

        var recipe = await _recipeRepository.GetByIdAsync(param.RecipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), param.RecipeId));
        if (recipe.ApprovalState == RecipeApprovalState.Released)
            return Result.Failure(Error.Conflict("Cannot modify params of a Released recipe."));

        var previousValue = param.ParamValue;
        param.UpdateValue(newValue);
        if (await _paramRepository.TryUpdateIfRecipeEditableAsync(param, ct))
            return Result.Success();

        param.UpdateValue(previousValue);
        return Result.Failure(Error.Conflict(
            "Recipe was released while its parameter was being updated."));
    }

    public async Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default)
    {
        var param = await _paramRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFoundOf(nameof(RecipeParam), paramId));

        var recipe = await _recipeRepository.GetByIdAsync(param.RecipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFoundOf(nameof(Recipe), param.RecipeId));
        if (recipe.ApprovalState == RecipeApprovalState.Released)
            return Result.Failure(Error.Conflict("Cannot modify params of a Released recipe."));

        return await _paramRepository.TryDeleteIfRecipeEditableAsync(paramId, ct)
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                "Recipe was released while its parameter was being deleted."));
    }

    public async Task<Result<Recipe>> CreateNewVersionAsync(
        string sourceRecipeId, string newRecipeId, CancellationToken ct = default)
    {
        var source = await _recipeRepository.GetByIdAsync(sourceRecipeId, ct);
        if (source is null)
            return Result.Failure<Recipe>(Error.NotFoundOf(nameof(Recipe), sourceRecipeId));
        if (source.ApprovalState != RecipeApprovalState.Released)
            return Result.Failure<Recipe>(Error.Conflict("Only Released recipes can have a new version."));

        var newVersion = source.CreateNewVersion(newRecipeId);
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

        await _recipeRepository.AddVersionAsync(newVersion, copiedParams, ct);
        return Result.Success(newVersion);
    }
}
