using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Application.Rms;

public interface IRecipeParamRepository
{
    Task<IReadOnlyList<RecipeParam>> GetByRecipeAsync(string recipeId, CancellationToken ct = default);
    Task<RecipeParam?> GetByIdAsync(string paramId, CancellationToken ct = default);
    Task AddAsync(RecipeParam param, CancellationToken ct = default);
    Task UpdateAsync(RecipeParam param, CancellationToken ct = default);
    Task DeleteAsync(string paramId, CancellationToken ct = default);
    Task<bool> TryAddIfRecipeEditableAsync(RecipeParam param, CancellationToken ct = default);
    Task<bool> TryUpdateIfRecipeEditableAsync(RecipeParam param, CancellationToken ct = default);
    Task<bool> TryDeleteIfRecipeEditableAsync(string paramId, CancellationToken ct = default);
}
