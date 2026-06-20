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

    public async Task<Result<RecipeDto>> CreateRecipeAsync(string recipeId, string name, string desc, string equipmentClassId, CancellationToken ct = default)
    {
        var r = await _service.CreateRecipeAsync(recipeId, name, desc, equipmentClassId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public Task<Result> RequestApprovalAsync(string recipeId, CancellationToken ct = default)
        => _service.RequestApprovalAsync(recipeId, ct);

    public Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default)
        => _service.Approve1Async(recipeId, approverId, ct);

    public Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default)
        => _service.Approve2Async(recipeId, approverId, ct);

    public Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default)
        => _service.ReleaseAsync(recipeId, releaserId, ct);

    public Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default)
        => _service.RejectAsync(recipeId, reason, ct);

    public async Task<Result<RecipeDto>> CreateNewVersionAsync(string sourceRecipeId, string newRecipeId, CancellationToken ct = default)
    {
        var r = await _service.CreateNewVersionAsync(sourceRecipeId, newRecipeId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeDto>(r.Error);
    }

    public async Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default)
        => (await _service.GetParamsAsync(recipeId, ct)).Select(ToDto).ToList();

    public async Task<Result<RecipeParamDto>> AddParamAsync(string paramId, string recipeId, string paramName, string paramValue, string unit, int sortOrder, CancellationToken ct = default)
    {
        var r = await _service.AddParamAsync(paramId, recipeId, paramName, paramValue, unit, sortOrder, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RecipeParamDto>(r.Error);
    }

    public Task<Result> UpdateParamAsync(string paramId, string newValue, CancellationToken ct = default)
        => _service.UpdateParamAsync(paramId, newValue, ct);

    public Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default)
        => _service.DeleteParamAsync(paramId, ct);

    private static RecipeDto ToDto(Recipe r)
        => new(r.Id, r.RecipeName, r.Description, r.EquipmentClassId, r.Version,
               r.ApprovalState.ToString(), r.FirstApproverId, r.SecondApproverId, r.ReleasedAt);

    private static RecipeParamDto ToDto(RecipeParam p)
        => new(p.Id, p.RecipeId, p.ParamName, p.ParamValue, p.Unit, p.SortOrder);
}
