using NexaOne.Common;

namespace NexaOne.ServiceContracts.Rms;

/// <summary>ADR-008 얇은 브리지 — RMS 레시피 승인 상태기계. plugin(RMS)이 구현, 호스트가 GetBean→캐스트로 DI 등록.
/// 상태위반은 Result(Error.Conflict)→409, 검증실패→400, NotFound→404로 매핑된다. 승인/배포자는 토큰 주체(비-부인성).</summary>
[NexaModuleBridge("Rms", "rmsRecipeBridge")]
public interface IRecipeApprovalBridge : INexaModuleBridge
{
    /// <summary>
    /// 레시피 목록의 optional 복합 필터. 기존 plugin 구현과의 호환을 위해 기본 구현은 기존 상태 조회 후
    /// 설비 클래스 필터를 적용하며, 최신 RMS plugin은 repository에서 두 조건을 함께 실행한다.
    /// </summary>
    async Task<IReadOnlyList<RecipeDto>> GetRecipesAsync(
        string? equipmentClassId = null,
        string? state = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<RecipeDto> rows = !string.IsNullOrWhiteSpace(state)
            ? await GetByStateAsync(state, ct)
            : await GetByEquipmentClassAsync(equipmentClassId ?? string.Empty, ct);

        return string.IsNullOrWhiteSpace(equipmentClassId)
            ? rows
            : rows.Where(recipe => recipe.EquipmentClassId.Equals(
                equipmentClassId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    Task<IReadOnlyList<RecipeDto>> GetByEquipmentClassAsync(string equipmentClassId, CancellationToken ct = default);
    Task<IReadOnlyList<RecipeDto>> GetByStateAsync(string state, CancellationToken ct = default);
    Task<Result<RecipeDto>> GetRecipeAsync(string recipeId, CancellationToken ct = default);
    Task<Result<RecipeDto>> CreateRecipeAsync(string recipeId, string name, string desc, string equipmentClassId, CancellationToken ct = default);
    Task<Result> RequestApprovalAsync(string recipeId, CancellationToken ct = default);
    Task<Result> Approve1Async(string recipeId, string approverId, CancellationToken ct = default);
    Task<Result> Approve2Async(string recipeId, string approverId, CancellationToken ct = default);
    Task<Result> ReleaseAsync(string recipeId, string releaserId, CancellationToken ct = default);
    Task<Result> RejectAsync(string recipeId, string reason, CancellationToken ct = default);
    Task<Result<RecipeDto>> CreateNewVersionAsync(string sourceRecipeId, string newRecipeId, CancellationToken ct = default);
    Task<IReadOnlyList<RecipeParamDto>> GetParamsAsync(string recipeId, CancellationToken ct = default);
    Task<Result<RecipeParamDto>> AddParamAsync(string paramId, string recipeId, string paramName, string paramValue, string unit, int sortOrder, CancellationToken ct = default);
    Task<Result> UpdateParamAsync(string paramId, string newValue, CancellationToken ct = default);
    Task<Result> DeleteParamAsync(string paramId, CancellationToken ct = default);
}
