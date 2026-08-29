using NexaOne.Common;
using NexaOne.RMS.Domain;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.RMS.Application.Rms;

/// <summary>ADR-008 얇은 브리지 어댑터 — RecipeService에 위임하고 도메인 엔티티를 계약 DTO로 매핑한다
/// (RecipeApprovalState enum→string). plugin ALC에서 생성되며 호스트가 IRecipeApprovalBridge로 캐스트해 DI 등록한다.</summary>
public sealed class RecipeBridge : IRecipeApprovalBridge
{
    private readonly RecipeService _service;

    public RecipeBridge(RecipeService service) => _service = service;

    public async Task<IReadOnlyList<RecipeDto>> GetRecipesAsync(
        string? equipmentClassId = null,
        string? state = null,
        CancellationToken ct = default)
    {
        RecipeApprovalState? parsedState = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<RecipeApprovalState>(state, ignoreCase: true, out var parsed))
                return Array.Empty<RecipeDto>();
            parsedState = parsed;
        }

        var result = await _service.GetRecipesAsync(equipmentClassId, parsedState, ct);
        return result.IsSuccess ? result.Value.Select(ToDto).ToList() : Array.Empty<RecipeDto>();
    }

    public async Task<IReadOnlyList<RecipeDto>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default)
    {
        var r = await _service.GetByEquipmentClassAsync(equipmentClassId, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<RecipeDto>();
    }

    public async Task<IReadOnlyList<RecipeDto>> GetByStateAsync(string state, CancellationToken ct = default)
    {
        // 호스트 컨트롤러가 유효 enum일 때만 호출하지만, 방어적으로 파싱 실패 시 빈 목록을 반환한다.
        if (!Enum.TryParse<RecipeApprovalState>(state, out var parsed))
            return new List<RecipeDto>();
        var r = await _service.GetByStateAsync(parsed, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<RecipeDto>();
    }

    public async Task<Result<RecipeDto>> GetRecipeAsync(string recipeId, CancellationToken ct = default)
    {
        var r = await _service.GetRecipeAsync(recipeId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public async Task<Result<RecipeDto>> CreateRecipeAsync(
        RecipeCreateCommand command, CancellationToken ct = default)
    {
        var r = await _service.CreateRecipeAsync(command, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public Task<Result> RequestApprovalAsync(
        string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => _service.RequestApprovalAsync(recipeId, context, ct);

    public Task<Result> Approve1Async(string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => _service.Approve1Async(recipeId, context, ct);

    public Task<Result> Approve2Async(string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => _service.Approve2Async(recipeId, context, ct);

    public Task<Result> ReleaseAsync(string recipeId, RecipeCommandContext context, CancellationToken ct = default)
        => _service.ReleaseAsync(recipeId, context, ct);

    public Task<Result> RejectAsync(
        string recipeId, string reason, RecipeCommandContext context, CancellationToken ct = default)
        => _service.RejectAsync(recipeId, reason, context, ct);

    public async Task<Result<RecipeDto>> CreateNewVersionAsync(
        RecipeVersionCreateCommand command, CancellationToken ct = default)
    {
        var r = await _service.CreateNewVersionAsync(command, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public async Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default)
        => (await _service.GetParamsAsync(recipeId, ct)).Select(ToDto).ToList();

    public async Task<Result<RecipeParamDto>> AddParamAsync(
        RecipeParamAddCommand command, CancellationToken ct = default)
    {
        var r = await _service.AddParamAsync(command, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeParamDto>(r.Error);
    }

    public Task<Result> UpdateParamAsync(RecipeParamUpdateCommand command, CancellationToken ct = default)
        => _service.UpdateParamAsync(command, ct);

    public Task<Result> DeleteParamAsync(
        RecipeParamDeleteCommand command, CancellationToken ct = default)
        => _service.DeleteParamAsync(command, ct);

    private static RecipeDto ToDto(Recipe r)
        => new(r.Id, r.RecipeName, r.Description, r.EquipmentClassId, r.Version,
               r.ApprovalState.ToString(), r.FirstApproverId, r.SecondApproverId, r.ReleasedAt);

    private static RecipeParamDto ToDto(RecipeParam p)
        => new(p.Id, p.RecipeId, p.ParamName, p.ParamValue, p.Unit, p.SortOrder, p.Version);
}
