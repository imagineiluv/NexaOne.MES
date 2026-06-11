using NexaOne.Common;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public record InterlockResult(bool IsTriggered, string Action, string Message)
{
    public static InterlockResult Pass() => new(false, string.Empty, string.Empty);
    public static InterlockResult Triggered(string action, string message) => new(true, action, message);
}

public class FdcInterlockService
{
    private readonly IFdcInterlockRuleRepository _ruleRepository;

    public FdcInterlockService(IFdcInterlockRuleRepository ruleRepository)
    {
        _ruleRepository = ruleRepository;
    }

    public async Task<IReadOnlyList<FdcInterlockRule>> GetRulesAsync(string equipmentId, CancellationToken ct = default)
        => await _ruleRepository.GetByEquipmentAsync(equipmentId, ct);

    public async Task<Result<FdcInterlockRule>> CreateRuleAsync(
        string ruleId, string ruleName, string equipmentId, string parameterId,
        string op, decimal threshold, string action, int priority,
        CancellationToken ct = default)
    {
        var result = FdcInterlockRule.Create(ruleId, ruleName, equipmentId, parameterId, op, threshold, action, priority);
        if (result.IsFailure) return result;
        await _ruleRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<InterlockResult> EvaluateAsync(
        string equipmentId,
        string parameterId,
        decimal value,
        CancellationToken ct = default)
    {
        var rules = await _ruleRepository.GetActiveRulesAsync(equipmentId, parameterId, ct);

        foreach (var rule in rules.OrderBy(r => r.Priority))
        {
            if (rule.Evaluate(value))
                return InterlockResult.Triggered(rule.Action, $"Rule '{rule.RuleName}' triggered: value {value} {rule.Operator} {rule.ThresholdValue}.");
        }

        return InterlockResult.Pass();
    }
}
