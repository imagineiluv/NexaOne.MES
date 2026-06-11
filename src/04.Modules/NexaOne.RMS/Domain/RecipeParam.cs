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

    public void UpdateValue(string newValue) => ParamValue = newValue;
}
