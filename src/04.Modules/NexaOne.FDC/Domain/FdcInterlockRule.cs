using NexaOne.Common;

namespace NexaOne.FDC.Domain;

public sealed class FdcInterlockRule : AuditableEntity<string>
{
    private FdcInterlockRule(string ruleId) : base(ruleId) { }

    public string RuleName { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string ParameterId { get; private set; } = string.Empty;
    public string Operator { get; private set; } = string.Empty;
    public decimal ThresholdValue { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public int Priority { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<FdcInterlockRule> Create(
        string ruleId,
        string ruleName,
        string equipmentId,
        string parameterId,
        string @operator,
        decimal thresholdValue,
        string action,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(ruleId), "Rule ID is required."));
        if (string.IsNullOrWhiteSpace(ruleName))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(ruleName), "Rule name is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(parameterId))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(parameterId), "Parameter ID is required."));
        if (@operator is not ("GT" or "LT" or "GTE" or "LTE" or "EQ"))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(@operator), "Operator must be GT, LT, GTE, LTE, or EQ."));
        if (string.IsNullOrWhiteSpace(action) || action.Length > 200)
            return Result.Failure<FdcInterlockRule>(Error.Validation(
                nameof(action), "Action key is required and must be 200 characters or fewer."));
        if (priority < 0)
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(priority), "Priority cannot be negative."));

        var rule = new FdcInterlockRule(ruleId)
        {
            RuleName = ruleName,
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            Operator = @operator,
            ThresholdValue = thresholdValue,
            Action = action,
            Priority = priority,
            IsActive = true
        };
        return rule;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 IsActive를 항상 true로 고정해, 비활성화(IsActive=false)된 룰이 읽기경로에서 활성으로
    /// 되살아나는 상태손실을 막는다(GetActiveRulesAsync는 SQL 필터로 가려졌으나 GetByEquipmentAsync는 실재 노출).</summary>
    public static FdcInterlockRule Restore(
        string ruleId, string ruleName, string equipmentId, string parameterId, string @operator,
        decimal thresholdValue, string action, int priority, bool isActive,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var rule = new FdcInterlockRule(ruleId)
        {
            RuleName = ruleName,
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            Operator = @operator,
            ThresholdValue = thresholdValue,
            Action = action,
            Priority = priority,
            IsActive = isActive
        };
        rule.RestoreAudit(createdBy ?? rule.CreatedBy, createdAt ?? rule.CreatedAt, updatedBy, updatedAt);
        return rule;
    }

    public bool Evaluate(decimal value) => Operator switch
    {
        "GT"  => value > ThresholdValue,
        "LT"  => value < ThresholdValue,
        "GTE" => value >= ThresholdValue,
        "LTE" => value <= ThresholdValue,
        "EQ"  => value == ThresholdValue,
        _     => false
    };

    public void Deactivate() => IsActive = false;
}
