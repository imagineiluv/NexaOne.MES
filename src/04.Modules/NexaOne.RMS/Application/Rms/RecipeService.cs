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
    {
        var list = await _recipeRepository.GetByEquipmentClassAsync(equipmentClassId, ct);
        return Result.Success(list);
    }

    public async Task<Result<IReadOnlyList<Recipe>>> GetByStateAsync(
        RecipeApprovalState state, CancellationToken ct = default)
    {
        var list = await _recipeRepository.GetByStateAsync(state, ct);
        return Result.Success(list);
    }

    public Task<int> GetCountByStateAsync(RecipeApprovalState state, CancellationToken ct = default)
        => _recipeRepository.GetCountByStateAsync(state, ct);

    public async Task<Result<Recipe>> GetRecipeAsync(string recipeId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        return recipe is null
            ? Result.Failure<Recipe>(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."))
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
            return Result.Failure(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."));

        var result = recipe.RequestApproval();
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."));

        var result = recipe.Approve1(approverId);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."));

        var result = recipe.Approve2(approverId);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."));

        var result = recipe.Release(releaserId);
        if (result.IsFailure) return result;

        await _recipeRepository.UpdateAsync(recipe, ct);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default)
    {
        var recipe = await _recipeRepository.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Result.Failure(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."));

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
            return Result.Failure<RecipeParam>(Error.NotFound(nameof(Recipe), $"Recipe '{recipeId}'을(를) 찾을 수 없습니다."));
        if (recipe.ApprovalState == RecipeApprovalState.Released)
            return Result.Failure<RecipeParam>(Error.Conflict("Cannot modify params of a Released recipe."));

        var result = RecipeParam.Create(paramId, recipeId, paramName, paramValue, unit, sortOrder);
        if (result.IsFailure) return result;
        await _paramRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> UpdateParamAsync(
        string paramId, string newValue, CancellationToken ct = default)
    {
        var param = await _paramRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFound(nameof(RecipeParam), $"RecipeParam '{paramId}'을(를) 찾을 수 없습니다."));

        param.UpdateValue(newValue);
        await _paramRepository.UpdateAsync(param, ct);
        return Result.Success();
    }

    public async Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default)
    {
        var param = await _paramRepository.GetByIdAsync(paramId, ct);
        if (param is null)
            return Result.Failure(Error.NotFound(nameof(RecipeParam), $"RecipeParam '{paramId}'을(를) 찾을 수 없습니다."));

        await _paramRepository.DeleteAsync(paramId, ct);
        return Result.Success();
    }

    public async Task<Result<Recipe>> CreateNewVersionAsync(
        string sourceRecipeId, string newRecipeId, CancellationToken ct = default)
    {
        var source = await _recipeRepository.GetByIdAsync(sourceRecipeId, ct);
        if (source is null)
            return Result.Failure<Recipe>(Error.NotFound(nameof(Recipe), $"Recipe '{sourceRecipeId}'을(를) 찾을 수 없습니다."));
        if (source.ApprovalState != RecipeApprovalState.Released)
            return Result.Failure<Recipe>(Error.Conflict("Only Released recipes can have a new version."));

        var newVersion = source.CreateNewVersion(newRecipeId);
        await _recipeRepository.AddAsync(newVersion, ct);
        return Result.Success(newVersion);
    }
}
