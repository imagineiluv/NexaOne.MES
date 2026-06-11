using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Application.Rms;

public interface IRecipeRepository
{
    Task<Recipe?> GetByIdAsync(string recipeId, CancellationToken ct = default);
    Task<IReadOnlyList<Recipe>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default);
    Task<IReadOnlyList<Recipe>> GetByStateAsync(RecipeApprovalState state, CancellationToken ct = default);
    Task AddAsync(Recipe recipe, CancellationToken ct = default);
    Task UpdateAsync(Recipe recipe, CancellationToken ct = default);
}
