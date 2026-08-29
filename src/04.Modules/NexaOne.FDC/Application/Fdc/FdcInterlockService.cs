using NexaOne.Common;
using NexaOne.FDC.Domain;
using NexaOne.ServiceContracts.Fdc;
using System.Collections.Immutable;

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
    private InterlockRuleSnapshot? _runtimeSnapshot;
    private int _runtimeRevision;

    internal event Action? RuntimeInvalidated;

    public FdcInterlockService(
        IFdcInterlockRuleRepository ruleRepository,
        IFdcInterlockHistoryRepository? historyRepository = null)
    {
        _ruleRepository = ruleRepository;
        _historyRepository = historyRepository;
    }

    public bool IsHistoryPersistenceConfigured => _historyRepository is not null;

    /// <summary>기동 시 검증된 불변 규칙 스냅샷이 현재 사용 가능한지 여부다.</summary>
    public bool IsRuntimeInitialized => Volatile.Read(ref _runtimeSnapshot) is not null;

    /// <summary>
    /// 수집 시작 전에 전체 설비 topology, 활성 규칙과 미해제 effect를 한 번에 로드한다.
    /// 모든 검증이 끝난 뒤에만 불변 스냅샷을 원자적으로 게시하므로 부분 로드 상태는 샘플 경로에 노출되지 않는다.
    /// </summary>
    public async Task<FdcInterlockRuntimeBootstrap> InitializeRuntimeAsync(
        IReadOnlyCollection<FdcInterlockTopology> topology,
        CancellationToken ct = default)
    {
        var revision = Volatile.Read(ref _runtimeRevision);
        Volatile.Write(ref _runtimeSnapshot, null);

        if (_historyRepository is null)
            throw new FdcInterlockRuntimeUnavailableException(
                "FDC interlock history repository is required for restart reconciliation.");
        if (topology is null || topology.Count == 0)
            throw new FdcInterlockRuntimeUnavailableException(
                "FDC equipment topology is empty or unavailable.");
        if (topology.Any(item => item is null || string.IsNullOrWhiteSpace(item.EquipmentId)))
            throw new FdcInterlockRuntimeUnavailableException(
                "Every FDC equipment topology entry requires an equipment ID.");

        var normalizedTopology = topology
            .GroupBy(item => item.EquipmentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(item => item.ParameterIds ?? Array.Empty<string>())
                    .Where(parameterId => !string.IsNullOrWhiteSpace(parameterId))
                    .ToImmutableHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        if (normalizedTopology.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value.Count == 0))
            throw new FdcInterlockRuntimeUnavailableException(
                "Every FDC equipment topology entry requires an equipment ID and at least one parameter.");

        var compiled = ImmutableDictionary.CreateBuilder<InterlockRuntimeKey, ImmutableArray<CompiledInterlockRule>>();
        var openEffects = new List<FdcInterlockHistory>();
        var requiredActions = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<FdcInterlockHistory> persistedOpenEffects;
        try
        {
            persistedOpenEffects = await _historyRepository.GetAllUnresolvedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FdcInterlockRuntimeUnavailableException(
                "The global unresolved interlock effect inventory is unavailable.", ex);
        }

        if (persistedOpenEffects is null)
            throw new FdcInterlockRuntimeUnavailableException(
                "The global unresolved interlock effect inventory returned no result.");

        var effectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in persistedOpenEffects)
        {
            if (string.IsNullOrWhiteSpace(effect.Id)
                || string.IsNullOrWhiteSpace(effect.RuleId)
                || string.IsNullOrWhiteSpace(effect.EquipmentId)
                || string.IsNullOrWhiteSpace(effect.ParameterId)
                || string.IsNullOrWhiteSpace(effect.Action))
                throw new FdcInterlockRuntimeUnavailableException(
                    "Every unresolved interlock effect requires nonblank effect, rule, equipment, parameter, and action IDs.");
            if (!effectIds.Add(effect.Id))
                throw new FdcInterlockRuntimeUnavailableException(
                    $"The global unresolved interlock inventory contains duplicate EffectId '{effect.Id}'.");
            if (!normalizedTopology.TryGetValue(effect.EquipmentId, out var effectParameters)
                || !effectParameters.Contains(effect.ParameterId))
                throw new FdcInterlockRuntimeUnavailableException(
                    $"Open effect '{effect.Id}' references '{effect.EquipmentId}/{effect.ParameterId}' outside the loaded topology.");

            openEffects.Add(effect);
            requiredActions.Add(effect.Action);
        }

        foreach (var (equipmentId, parameterIds) in normalizedTopology)
        {
            IReadOnlyList<FdcInterlockRule> persistedRules;
            try
            {
                persistedRules = await _ruleRepository.GetByEquipmentAsync(equipmentId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FdcInterlockRuntimeUnavailableException(
                    $"Active interlock rules for equipment '{equipmentId}' are unavailable.", ex);
            }

            if (persistedRules is null)
                throw new FdcInterlockRuntimeUnavailableException(
                    $"Active interlock rules for equipment '{equipmentId}' returned no result.");

            var activeRules = persistedRules.Where(rule => rule.IsActive).ToArray();
            if (activeRules.Length == 0)
                throw new FdcInterlockRuntimeUnavailableException(
                    $"Equipment '{equipmentId}' has no active interlock rule; run permit is denied.");

            foreach (var rule in activeRules)
            {
                var validation = FdcInterlockRule.Create(
                    rule.Id,
                    rule.RuleName,
                    rule.EquipmentId,
                    rule.ParameterId,
                    rule.Operator,
                    rule.ThresholdValue,
                    rule.Action,
                    rule.Priority);
                if (validation.IsFailure)
                    throw new FdcInterlockRuntimeUnavailableException(
                        $"Persisted active rule '{rule.Id}' is invalid: {validation.Error.Description}");
                if (!string.Equals(rule.EquipmentId, equipmentId, StringComparison.Ordinal))
                    throw new FdcInterlockRuntimeUnavailableException(
                        $"Rule '{rule.Id}' belongs to '{rule.EquipmentId}', not topology equipment '{equipmentId}'.");
                if (!parameterIds.Contains(rule.ParameterId))
                    throw new FdcInterlockRuntimeUnavailableException(
                        $"Rule '{rule.Id}' references parameter '{rule.ParameterId}' outside the loaded topology for '{equipmentId}'.");

                var key = new InterlockRuntimeKey(equipmentId, rule.ParameterId);
                var value = new CompiledInterlockRule(
                    rule.Id,
                    rule.RuleName,
                    rule.Operator,
                    rule.ThresholdValue,
                    rule.Action,
                    rule.Priority);
                compiled[key] = compiled.TryGetValue(key, out var existing)
                    ? existing.Add(value)
                    : ImmutableArray.Create(value);
                requiredActions.Add(rule.Action);
            }
        }

        var ordered = compiled.ToImmutableDictionary(
            item => item.Key,
            item => item.Value.OrderBy(rule => rule.Priority).ToImmutableArray());
        var snapshot = new InterlockRuleSnapshot(
            normalizedTopology.ToImmutableDictionary(StringComparer.Ordinal),
            ordered);
        foreach (var effect in openEffects)
        {
            if (!ordered.TryGetValue(new InterlockRuntimeKey(effect.EquipmentId, effect.ParameterId), out var rules)
                || !rules.Any(rule => string.Equals(rule.Id, effect.RuleId, StringComparison.Ordinal)
                                      && string.Equals(rule.Action, effect.Action, StringComparison.Ordinal)))
                throw new FdcInterlockRuntimeUnavailableException(
                    $"Open effect '{effect.Id}' no longer matches active rule/action " +
                    $"'{effect.RuleId}/{effect.Action}' for '{effect.EquipmentId}/{effect.ParameterId}'. " +
                    "Manual decommission evidence is required before startup.");
        }
        if (Volatile.Read(ref _runtimeRevision) != revision)
            throw new FdcInterlockRuntimeUnavailableException(
                "Interlock rules changed while the runtime snapshot was loading; explicit re-initialization is required.");
        Volatile.Write(ref _runtimeSnapshot, snapshot);

        return new FdcInterlockRuntimeBootstrap(
            openEffects.OrderBy(effect => effect.TriggeredAt).ThenBy(effect => effect.Id, StringComparer.Ordinal).ToArray(),
            requiredActions.OrderBy(action => action, StringComparer.Ordinal).ToArray(),
            revision);
    }

    /// <summary>
    /// Action adapter의 durable inventory에만 남은 effect를 동일 EffectId로 MES DB에 복구한다.
    /// DB의 Prepared INSERT 응답/트랜잭션이 유실된 뒤 물리 action만 적용된 crash window를 닫는 기동 경로다.
    /// </summary>
    public async Task<FdcInterlockHistory> ImportOutstandingEffectAsync(
        FdcInterlockOutstandingEffect outstanding,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outstanding);
        var request = outstanding.Request
            ?? throw new FdcInterlockRuntimeUnavailableException(
                "The action adapter returned an outstanding effect without its original request.");
        if (_historyRepository is null)
            throw new FdcInterlockRuntimeUnavailableException(
                "FDC interlock history repository is required to import action-adapter inventory.");
        if (string.IsNullOrWhiteSpace(request.EffectId)
            || string.IsNullOrWhiteSpace(request.RuleId)
            || string.IsNullOrWhiteSpace(request.EquipmentId)
            || string.IsNullOrWhiteSpace(request.ParameterId)
            || string.IsNullOrWhiteSpace(request.Action)
            || string.IsNullOrWhiteSpace(request.Message)
            || string.IsNullOrWhiteSpace(outstanding.ApplyAcknowledgementId)
            || request.TriggeredAt == default
            || outstanding.ApplyConfirmedAt == default
            || outstanding.ApplyConfirmedAt < request.TriggeredAt)
            throw new FdcInterlockRuntimeUnavailableException(
                $"Outstanding action effect '{request.EffectId}' has incomplete or inconsistent durable evidence.");

        var snapshot = GetRuntimeSnapshotFor(request.EquipmentId, request.ParameterId);
        if (!snapshot.Rules.TryGetValue(new InterlockRuntimeKey(request.EquipmentId, request.ParameterId), out var rules)
            || !rules.Any(rule => string.Equals(rule.Id, request.RuleId, StringComparison.Ordinal)
                                  && string.Equals(rule.Action, request.Action, StringComparison.Ordinal)))
            throw new FdcInterlockRuntimeUnavailableException(
                $"Outstanding action effect '{request.EffectId}' does not match the active rule/action snapshot.");

        var result = InterlockResult.Triggered(request.Action, request.Message, request.RuleId);
        var existing = await _historyRepository.GetByIdAsync(request.EffectId, ct);
        if (existing is { IsResolved: true })
            throw new FdcInterlockRuntimeUnavailableException(
                $"Outstanding physical effect '{request.EffectId}' conflicts with a resolved MES DB row.");
        if (existing is not null && !SameEpisode(
                existing,
                request.EquipmentId,
                request.ParameterId,
                request.TriggerValue,
                result,
                request.TriggeredAt))
            throw new FdcInterlockRuntimeUnavailableException(
                $"Outstanding physical effect '{request.EffectId}' conflicts with different MES DB evidence.");
        if (existing is not null
            && (!string.IsNullOrWhiteSpace(existing.ApplyAcknowledgementId)
                || existing.ApplyConfirmedAt is not null)
            && (existing.ApplyAcknowledgementId != outstanding.ApplyAcknowledgementId
                || existing.ApplyConfirmedAt != outstanding.ApplyConfirmedAt))
            throw new FdcInterlockRuntimeUnavailableException(
                $"Outstanding physical effect '{request.EffectId}' conflicts with MES DB apply acknowledgement evidence.");

        if (existing is null)
        {
            var recorded = await RecordTriggerAsync(
                request.EffectId,
                request.EquipmentId,
                request.ParameterId,
                request.TriggerValue,
                result,
                request.TriggeredAt,
                ct);
            if (recorded.IsFailure)
                throw new FdcInterlockRuntimeUnavailableException(
                    $"Outstanding physical effect '{request.EffectId}' could not be imported: " +
                    recorded.Error.Description);
        }

        var applied = FdcInterlockActionResult.Confirmed(outstanding.ApplyAcknowledgementId);
        if (!await MarkAppliedAsync(request.EffectId, applied, outstanding.ApplyConfirmedAt, ct))
            throw new FdcInterlockRuntimeUnavailableException(
                $"Outstanding physical effect '{request.EffectId}' apply evidence could not be persisted.");

        return await _historyRepository.GetByIdAsync(request.EffectId, ct)
               ?? throw new FdcInterlockRuntimeUnavailableException(
                   $"Imported physical effect '{request.EffectId}' is not visible after persistence.");
    }

    /// <summary>DB 접근 없이 기동 시 게시된 불변 규칙 스냅샷만 평가한다.</summary>
    public IReadOnlyList<InterlockResult> EvaluateRuntime(string equipmentId, string parameterId, decimal value)
    {
        var snapshot = GetRuntimeSnapshotFor(equipmentId, parameterId);

        if (!snapshot.Rules.TryGetValue(new InterlockRuntimeKey(equipmentId, parameterId), out var rules))
            return Array.Empty<InterlockResult>();

        return rules
            .Where(rule => rule.Evaluate(value))
            .Select(rule => InterlockResult.Triggered(
                    rule.Action,
                    $"Rule '{rule.RuleName}' triggered: value {value} {rule.Operator} {rule.ThresholdValue}.",
                    rule.Id))
            .ToArray();
    }

    /// <summary>현재 불변 snapshot에서 해당 입력이 하나 이상의 활성 인터락 규칙에 연결됐는지 확인한다.</summary>
    internal bool IsInterlockParameterRuntime(string equipmentId, string parameterId)
    {
        var snapshot = GetRuntimeSnapshotFor(equipmentId, parameterId);
        return snapshot.Rules.TryGetValue(new InterlockRuntimeKey(equipmentId, parameterId), out var rules)
               && rules.Length > 0;
    }

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
        if (IsRuntimeInitialized)
            return Result.Failure<FdcInterlockRule>(Error.Conflict(
                "Interlock rules cannot be changed while the runtime is active. Stop the FDC worker and restart after maintenance configuration is complete."));

        await _ruleRepository.AddAsync(result.Value, ct);
        // 비가동 구성 변경도 다음 InitializeRuntime이 새 revision을 명시적으로 검증하도록 표시한다.
        InvalidateRuntime();
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

    public async Task<bool> MarkAppliedAsync(string effectId, FdcInterlockActionResult result, DateTime confirmedAt, CancellationToken ct = default)
    {
        if (_historyRepository is null || !result.IsConfirmed) return false;
        var history = await _historyRepository.GetByIdAsync(effectId, ct);
        if (history is null || history.IsResolved) return false;
        if (history.EffectState >= FdcInterlockEffectState.Applied
            && history.ApplyAcknowledgementId == result.AcknowledgementId
            && history.ApplyConfirmedAt == confirmedAt)
            return true;
        var expected = history.Version;
        history.MarkApplied(result.AcknowledgementId, confirmedAt);
        return await UpdateOrObserveAsync(history, expected, ct);
    }

    public async Task<bool> MarkConditionNormalizedAsync(
        string effectId,
        DateTime normalizedAt,
        decimal normalizedValue,
        CancellationToken ct = default)
    {
        if (_historyRepository is null) return false;
        var history = await _historyRepository.GetByIdAsync(effectId, ct);
        if (history is null || history.IsResolved) return false;
        if (history.EffectState < FdcInterlockEffectState.Applied
            || string.IsNullOrWhiteSpace(history.ApplyAcknowledgementId)
            || history.ApplyConfirmedAt is null)
            return false;
        if (history.EffectState >= FdcInterlockEffectState.ConditionNormalized
            && history.ConditionNormalizedAt == normalizedAt
            && history.ConditionNormalizedValue == normalizedValue)
            return true;
        var expected = history.Version;
        history.MarkConditionNormalized(normalizedAt, normalizedValue);
        return await UpdateOrObserveAsync(history, expected, ct);
    }

    public async Task<bool> MarkReleasePendingAsync(string effectId, string? error, CancellationToken ct = default)
    {
        if (_historyRepository is null) return false;
        var history = await _historyRepository.GetByIdAsync(effectId, ct);
        if (history is null || history.IsResolved) return false;
        var expected = history.Version;
        history.MarkReleasePending(error);
        return await UpdateOrObserveAsync(history, expected, ct);
    }

    public async Task<bool> MarkActionErrorAsync(string effectId, string error, CancellationToken ct = default)
    {
        if (_historyRepository is null) return false;
        var history = await _historyRepository.GetByIdAsync(effectId, ct);
        if (history is null || history.IsResolved) return false;
        var expected = history.Version;
        history.MarkActionError(error);
        return await UpdateOrObserveAsync(history, expected, ct);
    }

    private static bool SameEpisode(
        FdcInterlockHistory history,
        string equipmentId,
        string parameterId,
        decimal value,
        InterlockResult result,
        DateTime? triggeredAt = null)
        => history.EquipmentId == equipmentId
           && history.ParameterId == parameterId
           && history.RuleId == result.RuleId
           && history.TriggerValue == value
           && history.Action == result.Action
           && history.Message == result.Message
           && (triggeredAt is null || history.TriggeredAt == triggeredAt.Value);

    /// <summary>한 위반 episode만 해제한다. 지연된 재시도가 뒤이어 발생한 새 episode까지 닫지 않도록
    /// stable effect ID로 대상을 제한하고, 프로젝트 adapter의 release ack/readback 없이는 terminal
    /// 상태로 전이하지 않는다. Parameter 범위 일괄 해제 API는 이 증거 경계를 우회하므로 제공하지 않는다.</summary>
    public async Task<int> ResolveEffectAsync(
        string effectId,
        string equipmentId,
        string parameterId,
        decimal value,
        DateTime releaseConfirmedAt,
        FdcInterlockReleaseResult releaseResult,
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
        if (history.IsResolved)
            return history.ReleaseAcknowledgementId == releaseResult.AcknowledgementId
                   && history.ReleaseConfirmedAt == releaseConfirmedAt
                ? 1
                : 0;

        var expectedVersion = history.Version;
        if (!releaseResult.IsConfirmed
            || history.EffectState is not FdcInterlockEffectState.ConditionNormalized
                and not FdcInterlockEffectState.ReleasePending
            || history.ConditionNormalizedAt is null
            || history.ConditionNormalizedValue is null)
            return 0;
        history.MarkReleaseConfirmed(releaseResult.AcknowledgementId, releaseConfirmedAt);
        history.Resolve(releaseConfirmedAt, value);
        return await UpdateOrObserveAsync(history, expectedVersion, ct) ? 1 : 0;
    }

    private async Task<bool> UpdateOrObserveAsync(
        FdcInterlockHistory history, int expectedVersion, CancellationToken ct)
    {
        if (_historyRepository is null) return false;
        if (await _historyRepository.UpdateAsync(history, expectedVersion, ct)) return true;

        // 0-row CAS may mean an ambiguous commit. Point-read only accepts the exact or newer durable state.
        var durable = await _historyRepository.GetByIdAsync(history.Id, ct);
        return durable is not null
               && durable.Version >= history.Version
               && durable.EffectState == history.EffectState
               && durable.IsResolved == history.IsResolved
               && durable.ResolvedAt == history.ResolvedAt
               && durable.ApplyAcknowledgementId == history.ApplyAcknowledgementId
               && durable.ApplyConfirmedAt == history.ApplyConfirmedAt
               && durable.ConditionNormalizedAt == history.ConditionNormalizedAt
               && durable.ConditionNormalizedValue == history.ConditionNormalizedValue
               && durable.ReleaseAcknowledgementId == history.ReleaseAcknowledgementId
               && durable.ReleaseConfirmedAt == history.ReleaseConfirmedAt
               && durable.LastError == history.LastError;
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

    private InterlockRuleSnapshot GetRuntimeSnapshotFor(string equipmentId, string parameterId)
    {
        var snapshot = Volatile.Read(ref _runtimeSnapshot)
            ?? throw new FdcInterlockRuntimeUnavailableException(
                "FDC interlock runtime is not initialized; run permit is denied.");

        if (!snapshot.Topology.TryGetValue(equipmentId, out var parameters)
            || !parameters.Contains(parameterId))
            throw new FdcInterlockRuntimeUnavailableException(
                $"Sample '{equipmentId}/{parameterId}' is outside the initialized FDC topology.");

        return snapshot;
    }

    internal bool IsRuntimeCurrent(int revision) =>
        Volatile.Read(ref _runtimeRevision) == revision
        && Volatile.Read(ref _runtimeSnapshot) is not null;

    private void InvalidateRuntime()
    {
        Interlocked.Increment(ref _runtimeRevision);
        Volatile.Write(ref _runtimeSnapshot, null);
        RuntimeInvalidated?.Invoke();
    }

    private sealed record InterlockRuleSnapshot(
        ImmutableDictionary<string, ImmutableHashSet<string>> Topology,
        ImmutableDictionary<InterlockRuntimeKey, ImmutableArray<CompiledInterlockRule>> Rules);

    private readonly record struct InterlockRuntimeKey(
        string EquipmentId,
        string ParameterId);

    private sealed record CompiledInterlockRule(
        string Id,
        string RuleName,
        string Operator,
        decimal ThresholdValue,
        string Action,
        int Priority)
    {
        public bool Evaluate(decimal value) => Operator switch
        {
            "GT" => value > ThresholdValue,
            "LT" => value < ThresholdValue,
            "GTE" => value >= ThresholdValue,
            "LTE" => value <= ThresholdValue,
            "EQ" => value == ThresholdValue,
            _ => false
        };
    }
}

public sealed record FdcInterlockTopology(
    string EquipmentId,
    IReadOnlyCollection<string> ParameterIds);

public sealed record FdcInterlockRuntimeBootstrap(
    IReadOnlyList<FdcInterlockHistory> OpenEffects,
    IReadOnlyCollection<string> RequiredActions,
    int Revision);

public class FdcInterlockRuntimeUnavailableException : InvalidOperationException
{
    public FdcInterlockRuntimeUnavailableException(string message) : base(message) { }

    public FdcInterlockRuntimeUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
