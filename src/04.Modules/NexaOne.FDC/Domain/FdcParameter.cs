using NexaOne.Common;

namespace NexaOne.FDC.Domain;

public sealed class FdcParameter : AuditableEntity<string>
{
    private FdcParameter(string parameterId) : base(parameterId) { }

    public string ParameterName { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string? EndpointId { get; private set; }
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
        decimal upperLimit,
        string? endpointId = null)
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
            EndpointId = endpointId,
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

    public Result AssignEndpoint(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return Result.Failure(Error.Validation(nameof(endpointId), "Endpoint ID is required."));

        EndpointId = endpointId;
        return Result.Success();
    }

    public void Deactivate() => IsActive = false;

    /// <summary>영속된 행을 검증 없이 도메인으로 재구성한다(읽기경로 Restore 패턴).
    /// Create+뮤테이터 경유 재구성은 (1) 영속 감사필드(CREATED_*/UPDATED_*)를 유실시키고
    /// (2) 검증 실패 시 행을 드롭하므로, 읽기경로는 이 팩토리를 사용한다.
    /// 비즈니스 필드는 그대로 세팅하고 감사 메타데이터는 RestoreAudit으로 일원 복원한다.</summary>
    public static FdcParameter Restore(
        string parameterId,
        string parameterName,
        string equipmentId,
        string? groupId,
        string unit,
        decimal lowerLimit,
        decimal upperLimit,
        decimal? lowerControlLimit,
        decimal? upperControlLimit,
        int samplingIntervalMs,
        bool isActive,
        string? createdBy = null,
        DateTime? createdAt = null,
        string? updatedBy = null,
        DateTime? updatedAt = null,
        string? endpointId = null)
    {
        var parameter = new FdcParameter(parameterId)
        {
            ParameterName = parameterName,
            EquipmentId = equipmentId,
            EndpointId = endpointId,
            GroupId = groupId,
            Unit = unit,
            LowerLimit = lowerLimit,
            UpperLimit = upperLimit,
            LowerControlLimit = lowerControlLimit,
            UpperControlLimit = upperControlLimit,
            SamplingIntervalMs = samplingIntervalMs,
            IsActive = isActive
        };
        parameter.RestoreAudit(createdBy ?? parameter.CreatedBy, createdAt ?? parameter.CreatedAt, updatedBy, updatedAt);
        return parameter;
    }
}
