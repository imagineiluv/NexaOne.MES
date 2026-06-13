using NexaOne.Common;

namespace NexaOne.FDC.Domain;

public sealed class FdcParameter : AuditableEntity<string>
{
    private FdcParameter(string parameterId) : base(parameterId) { }

    public string ParameterName { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string? GroupId { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public decimal LowerLimit { get; private set; }
    public decimal UpperLimit { get; private set; }
    public decimal? LowerControlLimit { get; private set; }
    public decimal? UpperControlLimit { get; private set; }
    public int SamplingIntervalMs { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<FdcParameter> Create(
        string parameterId,
        string parameterName,
        string equipmentId,
        string unit,
        decimal lowerLimit,
        decimal upperLimit)
    {
        if (string.IsNullOrWhiteSpace(parameterId))
            return Result.Failure<FdcParameter>(Error.Validation(nameof(parameterId), "Parameter ID is required."));
        if (string.IsNullOrWhiteSpace(parameterName))
            return Result.Failure<FdcParameter>(Error.Validation(nameof(parameterName), "Parameter name is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcParameter>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (lowerLimit >= upperLimit)
            return Result.Failure<FdcParameter>(Error.Validation(nameof(lowerLimit), "Lower limit must be less than upper limit."));

        var parameter = new FdcParameter(parameterId)
        {
            ParameterName = parameterName,
            EquipmentId = equipmentId,
            Unit = unit,
            LowerLimit = lowerLimit,
            UpperLimit = upperLimit,
            SamplingIntervalMs = 1000,
            IsActive = true
        };
        return parameter;
    }

    public Result UpdateLimits(decimal lower, decimal upper)
    {
        // Create와 동일한 불변식을 강제 — mutator 경유로 LowerLimit >= UpperLimit가 되어 OOS 오판정되는 것을 차단
        if (lower >= upper)
            return Result.Failure(Error.Validation(nameof(lower), "Lower limit must be less than upper limit."));
        LowerLimit = lower;
        UpperLimit = upper;
        return Result.Success();
    }

    public Result SetControlLimits(decimal lcl, decimal ucl)
    {
        if (lcl >= ucl)
            return Result.Failure(Error.Validation(nameof(lcl), "Lower control limit must be less than upper control limit."));
        LowerControlLimit = lcl;
        UpperControlLimit = ucl;
        return Result.Success();
    }

    /// <summary>파라미터를 그룹(FDC_PARAMETER_GROUP)에 배정한다. null이면 그룹 해제.</summary>
    public void AssignToGroup(string? groupId) => GroupId = groupId;

    public void Deactivate() => IsActive = false;
}
