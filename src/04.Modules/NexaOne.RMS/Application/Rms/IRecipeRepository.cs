using NexaOne.RMS.Domain;

namespace NexaOne.RMS.Application.Rms;

public interface IRecipeRepository
{
    Task<Recipe?> GetByIdAsync(string recipeId, CancellationToken ct = default);
    Task<IReadOnlyList<Recipe>> GetAsync(
        string? equipmentClassId,
        RecipeApprovalState? state,
        CancellationToken ct = default);
    Task<IReadOnlyList<Recipe>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default);
    Task<IReadOnlyList<Recipe>> GetByStateAsync(RecipeApprovalState state, CancellationToken ct = default);
    Task<int> GetCountByStateAsync(RecipeApprovalState state, CancellationToken ct = default);
    Task<RecipeWriteRecord?> GetWriteByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<bool> TryAddAsync(
        Recipe recipe,
        RecipeWriteRecord write,
        CancellationToken ct = default);
    Task<bool> TryAddVersionAsync(
        Recipe recipe,
        IReadOnlyList<RecipeParam> parameters,
        RecipeWriteRecord write,
        CancellationToken ct = default);
    Task<bool> TryTransitionAsync(
        Recipe recipe,
        RecipeApprovalState expectedState,
        RecipeTransitionWrite transition,
        CancellationToken ct = default);
    Task<RecipeApprovalHistoryRecord?> GetApprovalHistoryByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);
    Task<IReadOnlyList<RecipeApprovalHistoryRecord>> GetApprovalHistoryAsync(
        string recipeId, CancellationToken ct = default);
}

public sealed record RecipeApprovalHistoryRecord(
    string HistoryId,
    string IdempotencyKey,
    string RequestHash,
    string RecipeId,
    RecipeApprovalState FromState,
    RecipeApprovalState ToState,
    string ChangedBy,
    string? Reason,
    DateTime ChangedAt);

public sealed record RecipeTransitionWrite(
    string IdempotencyKey,
    string RequestHash,
    string ActorId,
    string? Reason);

public sealed record RecipeWriteRecord(
    string CommandId,
    string CommandType,
    string IdempotencyKey,
    string RequestHash,
    string RecipeId,
    string? SourceRecipeId,
    string ActorId,
    DateTime CreatedAt);
