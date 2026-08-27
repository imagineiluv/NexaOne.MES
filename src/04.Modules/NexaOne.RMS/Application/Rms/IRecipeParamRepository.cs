using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Application.Rms;

public interface IRecipeParamRepository
{
    Task<IReadOnlyList<RecipeParam>> GetByRecipeAsync(string recipeId, CancellationToken ct = default);
    Task<RecipeParam?> GetByIdAsync(string paramId, CancellationToken ct = default);
    Task<RecipeParamWriteRecord?> GetWriteByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<bool> TryAddAsync(
        RecipeParam param, RecipeParamWriteRecord write, CancellationToken ct = default);
    Task<bool> TryUpdateAsync(
        RecipeParamWriteRecord write, CancellationToken ct = default);
    Task<bool> TryDeleteAsync(
        RecipeParamWriteRecord write, CancellationToken ct = default);
}

public sealed record RecipeParamWriteRecord(
    string CommandId,
    string CommandType,
    string IdempotencyKey,
    string RequestHash,
    string ParamId,
    string RecipeId,
    string? ParamName,
    string? ParamValue,
    string? Unit,
    int? SortOrder,
    int? ExpectedVersion,
    int ResultVersion,
    string ChangedBy,
    DateTime ChangedAt);
