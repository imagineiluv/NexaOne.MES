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

    public void UpdateLimits(decimal lower, decimal upper)
    {
        LowerLimit = lower;
        UpperLimit = upper;
    }

    public void SetControlLimits(decimal lcl, decimal ucl)
    {
        LowerControlLimit = lcl;
        UpperControlLimit = ucl;
    }

    public void Deactivate() => IsActive = false;
}
