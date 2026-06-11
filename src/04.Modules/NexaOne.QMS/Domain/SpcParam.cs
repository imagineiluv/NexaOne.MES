using NexaOne.Common;

namespace NexaOne.QMS.Domain;

public sealed class SpcParam : AuditableEntity<string>
{
    private SpcParam(string paramId) : base(paramId) { }

    public string ParamName { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string ProcessId { get; private set; } = string.Empty;
    public decimal Mean { get; private set; }
    public decimal Ucl { get; private set; }
    public decimal Lcl { get; private set; }
    public decimal? Usl { get; private set; }
    public decimal? Lsl { get; private set; }
    public int SampleSize { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<SpcParam> Create(
        string paramId,
        string paramName,
        string equipmentId,
        string processId,
        decimal mean,
        decimal ucl,
        decimal lcl,
        int sampleSize,
        decimal? usl = null,
        decimal? lsl = null)
    {
        if (string.IsNullOrWhiteSpace(paramId))
            return Result.Failure<SpcParam>(Error.Validation(nameof(paramId), "Parameter ID is required."));
        if (string.IsNullOrWhiteSpace(paramName))
            return Result.Failure<SpcParam>(Error.Validation(nameof(paramName), "Parameter name is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<SpcParam>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(processId))
            return Result.Failure<SpcParam>(Error.Validation(nameof(processId), "Process ID is required."));
        if (ucl <= lcl)
            return Result.Failure<SpcParam>(Error.Validation(nameof(ucl), "UCL must be greater than LCL."));
        if (sampleSize <= 0)
            return Result.Failure<SpcParam>(Error.Validation(nameof(sampleSize), "Sample size must be positive."));

        var param = new SpcParam(paramId)
        {
            ParamName = paramName,
            EquipmentId = equipmentId,
            ProcessId = processId,
            Mean = mean,
            Ucl = ucl,
            Lcl = lcl,
            Usl = usl,
            Lsl = lsl,
            SampleSize = sampleSize,
            IsActive = true
        };
        return param;
    }

    public Result UpdateControlLimits(decimal mean, decimal ucl, decimal lcl)
    {
        if (ucl <= lcl)
            return Result.Failure(Error.Validation(nameof(ucl), "UCL must be greater than LCL."));

        Mean = mean;
        Ucl = ucl;
        Lcl = lcl;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
