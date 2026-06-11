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
        if (@operator is not ("GT" or "LT" or "GTE" or "LTE" or "EQ"))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(@operator), "Operator must be GT, LT, GTE, LTE, or EQ."));
        if (action is not ("STOP" or "ALARM" or "NOTIFY"))
            return Result.Failure<FdcInterlockRule>(Error.Validation(nameof(action), "Action must be STOP, ALARM, or NOTIFY."));

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
