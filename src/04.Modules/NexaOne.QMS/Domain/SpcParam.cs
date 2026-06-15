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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 IsActive를 항상 true로 강제해, 비활성화된(IsActive=false) 파라미터가 읽기경로에서 활성으로
    /// 오인되는 상태손실을 막는다(GetByEquipment는 SQL의 IS_ACTIVE=1 필터로 가려졌으나 손실 자체는 실재).</summary>
    public static SpcParam Restore(
        string paramId, string paramName, string equipmentId, string processId,
        decimal mean, decimal ucl, decimal lcl, decimal? usl, decimal? lsl,
        int sampleSize, bool isActive)
        => new(paramId)
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
            IsActive = isActive
        };

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
