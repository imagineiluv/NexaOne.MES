using NexaOne.Common;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

public record InterlockResult(bool IsTriggered, string Action, string Message, string? RuleId = null)
{
    public static InterlockResult Pass() => new(false, string.Empty, string.Empty);
    public static InterlockResult Triggered(string action, string message, string? ruleId = null)
        => new(true, action, message, ruleId);
}

public class FdcInterlockService
{
    private readonly IFdcInterlockRuleRepository _ruleRepository;
    private readonly IFdcInterlockHistoryRepository? _historyRepository;

    public FdcInterlockService(
        IFdcInterlockRuleRepository ruleRepository,
        IFdcInterlockHistoryRepository? historyRepository = null)
    {
        _ruleRepository = ruleRepository;
        _historyRepository = historyRepository;
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
                return InterlockResult.Triggered(
                    rule.Action,
                    $"Rule '{rule.RuleName}' triggered: value {value} {rule.Operator} {rule.ThresholdValue}.",
                    rule.Id);
        }

        return InterlockResult.Pass();
    }

    /// <summary>발동한 인터락을 FDC_INTERLOCK_HISTORY에 1행 기록한다.
    /// 이력 리포지토리가 주입되지 않았거나(no-op) 미발동·RuleId 부재 시 기록하지 않는다.</summary>
    public async Task<Result<FdcInterlockHistory>> RecordTriggerAsync(
        string equipmentId,
        string parameterId,
        decimal value,
        InterlockResult result,
        CancellationToken ct = default)
    {
        if (!result.IsTriggered || string.IsNullOrWhiteSpace(result.RuleId))
            return Result.Failure<FdcInterlockHistory>(
                Error.Validation(nameof(result), "Interlock result is not a triggered event with a rule."));
        if (_historyRepository is null)
            return Result.Failure<FdcInterlockHistory>(
                Error.Validation(nameof(_historyRepository), "History repository is not configured."));

        var history = FdcInterlockHistory.Create(
            Guid.NewGuid().ToString("N"), result.RuleId!, equipmentId, parameterId,
            value, result.Action, result.Message, DateTime.UtcNow);
        if (history.IsFailure) return history;

        await _historyRepository.AddAsync(history.Value, ct);
        return history;
    }
}
