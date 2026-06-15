using NexaOne.Common;

namespace NexaOne.RMS.Domain;

public sealed class RecipeParam : Entity<string>
{
    private RecipeParam(string paramId) : base(paramId) { }

    public string RecipeId { get; private set; } = string.Empty;
    public string ParamName { get; private set; } = string.Empty;
    public string ParamValue { get; private set; } = string.Empty;
    public string Unit { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }

    public static Result<RecipeParam> Create(
        string paramId,
        string recipeId,
        string paramName,
        string paramValue,
        string unit,
        int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(paramId))
            return Result.Failure<RecipeParam>(Error.Validation(nameof(paramId), "Parameter ID is required."));
        if (string.IsNullOrWhiteSpace(recipeId))
            return Result.Failure<RecipeParam>(Error.Validation(nameof(recipeId), "Recipe ID is required."));
        if (string.IsNullOrWhiteSpace(paramName))
            return Result.Failure<RecipeParam>(Error.Validation(nameof(paramName), "Parameter name is required."));

        var param = new RecipeParam(paramId)
        {
            RecipeId = recipeId,
            ParamName = paramName,
            ParamValue = paramValue,
            Unit = unit,
            SortOrder = sortOrder
        };
        return param;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 paramId/recipeId/paramName이 공백이면 실패(Result.Failure)를 돌려, ToDomain의 .Value가
    /// null이 되어 해당 행이 읽기경로에서 통째로 유실된다(GetByRecipe의 OfType 필터가 이 null을 가린다).
    /// Restore는 영속된 값을 그대로 재구성해 이 상태손실을 막는다.</summary>
    public static RecipeParam Restore(
        string paramId, string recipeId, string paramName, string paramValue, string unit, int sortOrder)
        => new(paramId)
        {
            RecipeId = recipeId,
            ParamName = paramName,
            ParamValue = paramValue,
            Unit = unit,
            SortOrder = sortOrder
        };

    public void UpdateValue(string newValue) => ParamValue = newValue;
}
