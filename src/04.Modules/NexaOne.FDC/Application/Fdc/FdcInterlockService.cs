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

    public bool IsHistoryPersistenceConfigured => _historyRepository is not null;

    public async Task<IReadOnlyList<FdcInterlockRule>> GetRulesAsync(string equipmentId, CancellationToken ct = default)
        => await _ruleRepository.GetByEquipmentAsync(equipmentId, ct);

    /// <summary>설비의 인터락 발동 이력을 기간으로 조회한다(이력 리포지토리 미구성 시 빈 목록).</summary>
    public async Task<IReadOnlyList<FdcInterlockHistory>> GetHistoryAsync(
        string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
        => _historyRepository is null
            ? Array.Empty<FdcInterlockHistory>()
            : await _historyRepository.GetByEquipmentAsync(equipmentId, from, to, ct);

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
    public Task<Result<FdcInterlockHistory>> RecordTriggerAsync(
        string equipmentId,
        string parameterId,
        decimal value,
        InterlockResult result,
        CancellationToken ct = default)
        => RecordTriggerAsync(
            Guid.NewGuid().ToString("N"), equipmentId, parameterId, value, result, DateTime.UtcNow, ct);

    /// <summary>수집기가 만든 stable effect ID와 최초 감지 시각으로 발동 이력을 기록한다.
    /// 기록 재시도에서도 같은 ID/시각을 사용해 한 위반 episode가 새 이벤트로 바뀌지 않게 한다.</summary>
    public async Task<Result<FdcInterlockHistory>> RecordTriggerAsync(
        string effectId,
        string equipmentId,
        string parameterId,
        decimal value,
        InterlockResult result,
        DateTime triggeredAt,
        CancellationToken ct = default)
    {
        if (!result.IsTriggered || string.IsNullOrWhiteSpace(result.RuleId))
            return Result.Failure<FdcInterlockHistory>(
                Error.Validation(nameof(result), "Interlock result is not a triggered event with a rule."));
        if (_historyRepository is null)
            return Result.Failure<FdcInterlockHistory>(
                Error.Validation(nameof(_historyRepository), "History repository is not configured."));

        // 첫 INSERT 응답이 유실된 ambiguous commit 뒤에도 같은 EffectId 재시도가 PK 충돌에 고착되지 않도록
        // 먼저 durable 승자를 확인한다. interlock 발생은 희소 경로라 PK point-read 비용보다 증거 보존을 우선한다.
        var existing = await _historyRepository.GetByIdAsync(effectId, ct);
        if (existing is not null)
        {
            if (SameEpisode(existing, equipmentId, parameterId, value, result))
                return Result.Success(existing);

            return Result.Failure<FdcInterlockHistory>(Error.Conflict(
                $"Effect ID '{effectId}' is already used by a different interlock episode."));
        }

        var history = FdcInterlockHistory.Create(
            effectId, result.RuleId!, equipmentId, parameterId,
            value, result.Action, result.Message, triggeredAt);
        if (history.IsFailure) return history;

        await _historyRepository.AddAsync(history.Value, ct);
        return history;
    }

    private static bool SameEpisode(
        FdcInterlockHistory history,
        string equipmentId,
        string parameterId,
        decimal value,
        InterlockResult result)
        => history.EquipmentId == equipmentId
           && history.ParameterId == parameterId
           && history.RuleId == result.RuleId
           && history.TriggerValue == value
           && history.Action == result.Action
           && history.Message == result.Message;

    /// <summary>해당 설비·파라미터의 미해제 인터락 이력을 해제(Resolve)한다 — 값이 정상 범위로 복귀했을 때.
    /// 해제한 이력 건수를 반환한다(이력 리포지토리 미구성 시 0).</summary>
    public async Task<int> ResolveActiveAsync(
        string equipmentId,
        string parameterId,
        CancellationToken ct = default)
    {
        if (_historyRepository is null) return 0;

        var targets = await _historyRepository.GetUnresolvedAsync(equipmentId, parameterId, ct);
        foreach (var history in targets)
        {
            history.Resolve(DateTime.UtcNow);
            await _historyRepository.UpdateAsync(history, ct);
        }
        return targets.Count;
    }

    /// <summary>한 위반 episode만 해제한다. 지연된 재시도가 뒤이어 발생한 새 episode까지 닫지 않도록
    /// stable effect ID로 대상을 제한한다.</summary>
    public async Task<int> ResolveEffectAsync(
        string effectId,
        string equipmentId,
        string parameterId,
        decimal value,
        DateTime resolvedAt,
        CancellationToken ct = default)
    {
        if (_historyRepository is null) return 0;

        // UPDATE가 commit됐지만 응답만 유실된 경우 재시도 point-read는 이미 resolved인 행을 반환한다.
        // 이를 idempotent success로 수렴시켜 collector pending이 영구 잔류하지 않게 한다.
        var history = await _historyRepository.GetByIdAsync(effectId, ct);
        if (history is null
            || history.EquipmentId != equipmentId
            || history.ParameterId != parameterId)
            return 0;
        if (history.IsResolved) return 1;

        history.Resolve(resolvedAt, value);
        await _historyRepository.UpdateAsync(history, ct);
        return 1;
    }

    /// <summary>프로세스 재시작 뒤 가장 최근의 durable 미해제 episode를 반환한다.</summary>
    public async Task<FdcInterlockHistory?> GetLatestUnresolvedAsync(
        string equipmentId,
        string parameterId,
        CancellationToken ct = default)
        => _historyRepository is null
            ? null
            : (await _historyRepository.GetUnresolvedAsync(equipmentId, parameterId, ct)).FirstOrDefault();

    /// <summary>
    /// 프로세스 재시작 뒤 수집기의 메모리 상태를 복원할 수 있도록 해당 태그의 durable 미해제
    /// 인터락 존재 여부를 반환한다. 이력 저장소가 없는 경량 구성에서는 false다.
    /// </summary>
    public async Task<bool> HasUnresolvedAsync(
        string equipmentId,
        string parameterId,
        CancellationToken ct = default)
        => await GetLatestUnresolvedAsync(equipmentId, parameterId, ct) is not null;
}
