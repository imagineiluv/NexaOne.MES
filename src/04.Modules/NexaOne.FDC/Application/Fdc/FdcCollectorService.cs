using System.Collections.Concurrent;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.Common.Telemetry;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>
/// 정규화된 설비 샘플을 FDC 수집 데이터 적재로 잇는 오케스트레이터 (§10.4).
/// <see cref="FdcDataService"/>로 FDC_TB_COLLECT_DATA에 기록한다.
/// </summary>
/// <remarks>
/// 설비 lifecycle과 PLC 구독은 Infrastructure 어댑터가 담당한다. 파라미터 미정의·검증 실패는
/// 수집 루프를 막지 않도록 예외를 전파하지 않는다.
/// </remarks>
public sealed class FdcCollectorService
{
    private readonly FdcDataService _dataService;
    private readonly FdcInterlockService? _interlockService;
    private readonly FdcAlarmService? _alarmService;
    private readonly IFdcInterlockActionPort? _actionPort;
    private readonly TimeSpan _actionTimeout;
    private readonly bool _requireRuntimeAuthority;
    private readonly object _runtimeStateGate = new();
    private readonly HashSet<FdcRuntimeKey> _pendingInitialSnapshots = new();
    private int? _preparedRuntimeRevision;
    private int _runtimeOperational;
    private int _runPermit;
    private int _activeEffectCount;
    private int _pendingResolutionCount;
    private Exception? _runtimeFault;
    private FdcRuntimeAuthority? _runtimeAuthority;

    // 현재 발동 중인 (설비|파라미터)의 모든 episode. 잘못 중복 생성된 durable open 행도 재시작 때
    // EffectId별로 빠짐없이 action 재조정하고 정상 복귀 시 각각 해제한다.
    private readonly ConcurrentDictionary<FdcRuntimeKey, List<ActiveInterlockEpisode>> _activeInterlocks = new();
    // 정상 복귀 신호 뒤 DB 해제가 실패한 episode. 다음 Good 샘플에서 이력 해제만 재시도한다.
    private readonly ConcurrentDictionary<FdcRuntimeKey, Queue<PendingInterlockResolution>> _pendingInterlockResolutions = new();
    // 현재 발생 중인 알람을 설정 ID별 episode로 유지한다. 같은 parameter의 다른 규칙이 계속
    // hit하더라도 정상화된 설정만 독립적으로 clear할 수 있어야 한다.
    private readonly ConcurrentDictionary<FdcRuntimeKey, Dictionary<string, string>> _activeAlarms = new();
    private readonly ConcurrentDictionary<FdcRuntimeKey, byte> _loadedAlarmStates = new();
    // (설비|파라미터) 키별 평가-기록-해제 직렬화 — 동시 태그 이벤트의 발동↔해제 순서 역전 방지
    private readonly ConcurrentDictionary<FdcRuntimeKey, SemaphoreSlim> _keyGates = new();

    /// <summary>인터락 규칙의 프로젝트 action이 ack/readback까지 확인된 뒤 episode당 한 번 발생한다.
    /// Prepared/Applied DB 증거는 먼저 시도하되 장애 시 같은 EffectId로 재시도한다. 공통 FDC의 버스 알림은
    /// 장치 제어 출력이 아니다 (§10.4.2).</summary>
    public event EventHandler<FdcInterlockTriggeredEventArgs>? InterlockTriggered;

    /// <summary>발동했던 인터락이 정상 범위 복귀로 해제됐을 때 발생한다 (§10.4.2).</summary>
    public event EventHandler<FdcInterlockResolvedEventArgs>? InterlockResolved;

    /// <summary>임계치 알람이 발생했을 때 발생한다 (§10.4.1). 후속 알림은 호스트 구독자가 처리한다.</summary>
    public event EventHandler<FdcAlarmRaisedEventArgs>? AlarmRaised;

    /// <summary>발생했던 알람이 정상 범위 복귀로 해제됐을 때 발생한다 (§10.4.1).</summary>
    public event EventHandler<FdcAlarmClearedEventArgs>? AlarmCleared;

    /// <summary>
    /// Runtime-only supervision signal. The FDC worker uses it to close its driver sessions after a
    /// fatal runtime loss (bad input quality, invalidated rules, or an unconfirmed apply/reconcile).
    /// An ordinary automatic-run hold caused by an active effect does not stop monitoring.
    /// </summary>
    internal event Action<Exception>? RuntimeFaulted;

    public FdcCollectorService(
        FdcDataService dataService,
        FdcInterlockService? interlockService = null,
        FdcAlarmService? alarmService = null,
        IFdcInterlockActionPort? actionPort = null,
        TimeSpan? actionTimeout = null,
        bool requireRuntimeAuthority = false)
    {
        _dataService = dataService;
        _interlockService = interlockService;
        _alarmService = alarmService;
        _actionPort = actionPort;
        _requireRuntimeAuthority = requireRuntimeAuthority;
        _actionTimeout = actionTimeout ?? TimeSpan.FromSeconds(10);
        if (_actionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(actionTimeout), _actionTimeout, "Interlock action timeout must be positive.");
        _runtimeOperational = interlockService is null ? 1 : 0;
        _runPermit = interlockService is null ? 1 : 0;
        if (interlockService is not null)
            interlockService.RuntimeInvalidated += OnInterlockRuntimeInvalidated;
    }

    internal void BindRuntimeAuthority(FdcRuntimeAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority.OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority.ConfigRevision);
        if (authority.FenceToken <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(authority), authority.FenceToken, "Runtime fence token must be positive.");

        lock (_runtimeStateGate)
        {
            if (_runtimeAuthority is { } current
                && (current.OwnerId != authority.OwnerId
                    || current.FenceToken != authority.FenceToken
                    || current.ConfigRevision != authority.ConfigRevision))
                throw new FdcInterlockRuntimeUnavailableException(
                    "FDC runtime authority identity changed without a full worker restart.");

            _runtimeAuthority = authority;
        }
    }

    internal void ClearRuntimeAuthority()
    {
        lock (_runtimeStateGate)
            _runtimeAuthority = null;
    }

    /// <summary>
    /// 기동 시 topology/규칙/open effect/action adapter를 모두 검증하고, 모든 열린 EffectId가 실제 장치
    /// readback까지 재확인된 뒤에만 운전을 허가한다.
    /// </summary>
    public async Task InitializeInterlockRuntimeAsync(
        IReadOnlyCollection<FdcInterlockTopology> topology,
        CancellationToken ct = default)
    {
        lock (_runtimeStateGate)
        {
            _runtimeOperational = 0;
            _runPermit = 0;
            _activeEffectCount = 0;
            _pendingResolutionCount = 0;
            _runtimeFault = null;
            _preparedRuntimeRevision = null;
            _pendingInitialSnapshots.Clear();
        }
        _activeInterlocks.Clear();
        _pendingInterlockResolutions.Clear();
        _activeAlarms.Clear();
        _loadedAlarmStates.Clear();

        FdcInterlockRuntimeBootstrap? bootstrap = null;
        if (_interlockService is not null)
        {
            if (_requireRuntimeAuthority && GetRuntimeAuthority() is null)
                throw new FdcInterlockRuntimeUnavailableException(
                    "A durable FDC runtime lease/fence authority is required before interlock initialization.");
            if (_actionPort is null)
                throw new FdcInterlockRuntimeUnavailableException(
                    "A project-owned IFdcInterlockActionPort is required; run permit is denied.");

            bootstrap = await _interlockService.InitializeRuntimeAsync(topology, ct);
            FdcInterlockActionReadiness readiness;
            try
            {
                readiness = await AwaitActionPortAsync(
                    token => _actionPort.CheckReadyAsync(bootstrap.RequiredActions, token),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FdcInterlockRuntimeUnavailableException(
                    "The project interlock action adapter readiness check failed; run permit is denied.", ex);
            }

            if (readiness is null
                || !readiness.IsAvailable
                || !readiness.CancellationFencingConfirmed
                || !readiness.AggregateEffectOwnershipConfirmed
                || (_requireRuntimeAuthority && !readiness.RuntimeFencePersistenceConfirmed))
                throw new FdcInterlockRuntimeUnavailableException(
                    "The project interlock action adapter is unavailable or did not confirm "
                    + "cancellation/deadline fencing, shared-output aggregate EffectId ownership, "
                    + "and durable runtime-fence rejection: "
                    + $"{readiness?.Detail ?? "no result"}.");

            if (readiness.OutstandingEffects is null)
                throw new FdcInterlockRuntimeUnavailableException(
                    "The project interlock action adapter returned no durable outstanding-effect inventory.");

            var openEffects = bootstrap.OpenEffects.ToDictionary(effect => effect.Id, StringComparer.Ordinal);
            var adapterEffectIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var outstanding in readiness.OutstandingEffects)
            {
                if (outstanding?.Request is null
                    || !adapterEffectIds.Add(outstanding.Request.EffectId))
                    throw new FdcInterlockRuntimeUnavailableException(
                        "The project interlock action adapter returned a null or duplicate outstanding EffectId.");

                var imported = await _interlockService.ImportOutstandingEffectAsync(outstanding, ct);
                openEffects[imported.Id] = imported;
            }

            // Never trust a persisted ConditionNormalized/ReleasePending state before current PLC input is known.
            // Reassert every unresolved physical effect first; the initial Good snapshot below is the sole release gate.
            foreach (var history in openEffects.Values
                         .OrderBy(effect => effect.TriggeredAt)
                         .ThenBy(effect => effect.Id, StringComparer.Ordinal))
            {
                var episode = new ActiveInterlockEpisode(
                    history.Id,
                    history.TriggerValue,
                    InterlockResult.Triggered(history.Action, history.Message, history.RuleId),
                    history.TriggeredAt,
                    historyPending: false);
                await ApplyActionAsync(history.EquipmentId, history.ParameterId, episode, isRecovery: true, reconcile: true, ct);
                AddActiveEpisode(RuntimeKey(history.EquipmentId, history.ParameterId), episode);
                RaiseInterlockTriggered(history.EquipmentId, history.ParameterId, episode, isRecovery: true);
            }
        }

        await PreloadAlarmStateAsync(topology, ct);

        lock (_runtimeStateGate)
        {
            if (_interlockService is not null && !_interlockService.IsRuntimeCurrent(bootstrap!.Revision))
                throw new FdcInterlockRuntimeUnavailableException(
                    "Interlock rules changed during action readiness/reconciliation; explicit re-initialization is required.");

            _preparedRuntimeRevision = bootstrap?.Revision;
            foreach (var equipment in topology)
            {
                foreach (var parameterId in equipment.ParameterIds)
                    _pendingInitialSnapshots.Add(RuntimeKey(equipment.EquipmentId, parameterId));
            }
        }
    }

    /// <summary>
    /// 열린 PLC 연결에서 명시적으로 읽은 현재값을 평가한다. 이 단계는 아직 run permit을 요구하지 않지만
    /// 동일한 action ack/readback과 telemetry ordering을 적용한다.
    /// </summary>
    public async Task EvaluateInitialSnapshotAsync(
        string equipmentId,
        IReadOnlyCollection<FdcTagSample> samples,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        EnsureRuntimePreparedForInitialSnapshot();

        foreach (var sample in samples)
            await ProcessSampleAsync(
                equipmentId, sample, requireOperationalRuntime: false, markInitialSnapshot: true, ct);
    }

    /// <summary>
    /// 모든 활성 topology 값의 초기 평가가 끝난 뒤 run permit을 원자적으로 게시한다.
    /// callback 버퍼를 소유한 worker가 그 버퍼 gate 안에서 호출해야 snapshot과 live stream 사이에 틈이 없다.
    /// </summary>
    public void CompleteInterlockRuntimeInitialization()
    {
        lock (_runtimeStateGate)
        {
            if (_interlockService is null)
            {
                _runtimeOperational = 1;
                _runPermit = 1;
                return;
            }

            if (_preparedRuntimeRevision is null || !_interlockService.IsRuntimeCurrent(_preparedRuntimeRevision.Value))
                throw new FdcInterlockRuntimeUnavailableException(
                    "FDC interlock runtime is not prepared or changed during initial snapshot evaluation.");

            if (_pendingInitialSnapshots.Count > 0)
            {
                var missing = string.Join(", ", _pendingInitialSnapshots
                    .Select(static key => $"{key.EquipmentId}/{key.ParameterId}")
                    .OrderBy(static key => key, StringComparer.Ordinal));
                throw new FdcInterlockRuntimeUnavailableException(
                    $"FDC initial snapshot is incomplete; missing active parameters: {missing}.");
            }

            _runtimeOperational = 1;
            _runPermit = _activeEffectCount == 0 && _pendingResolutionCount == 0 ? 1 : 0;
        }
    }

    /// <summary>규칙·topology·action adapter·모든 open effect가 검증된 경우에만 true다.</summary>
    public bool IsRunPermitted => _interlockService is null || Volatile.Read(ref _runPermit) == 1;

    /// <summary>태그 변경 1건을 인터락 스냅샷으로 먼저 평가·적용한 뒤 수집 데이터로 적재한다.
    /// telemetry DB 지연/장애가 프로젝트 action 실행을 선행 차단하지 않는다.</summary>
    public async Task OnTagChangeAsync(string equipmentId, FdcTagSample sample, CancellationToken ct = default)
        => await ProcessSampleAsync(equipmentId, sample, requireOperationalRuntime: true, markInitialSnapshot: false, ct);

    internal async Task OnBufferedStartupTagChangeAsync(
        string equipmentId,
        FdcTagSample sample,
        CancellationToken ct = default)
        => await ProcessSampleAsync(equipmentId, sample, requireOperationalRuntime: false, markInitialSnapshot: false, ct);

    internal void DenyRunPermit(Exception? cause = null)
        => FailRuntime(cause);

    /// <summary>
    /// Driver sample 변화와 무관하게 Prepared/Applied/ReleasePending 영속화를 재시도한다.
    /// Worker supervisor만 호출하며 각 parameter gate로 live callback과 직렬화한다.
    /// </summary>
    internal async Task RetryPendingEffectPersistenceAsync(CancellationToken ct = default)
    {
        foreach (var key in _activeInterlocks.Keys
                     .Concat(_pendingInterlockResolutions.Keys)
                     .Distinct())
        {
            var gate = _keyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (_activeInterlocks.TryGetValue(key, out var episodes))
                {
                    foreach (var episode in episodes.ToArray())
                    {
                        await RecordPendingInterlockAsync(key.EquipmentId, key.ParameterId, episode, ct);
                        if (episode.PendingRelease is not null)
                            await PersistPendingReleaseEvidenceAsync(episode, ct);
                    }
                }
                await RetryPendingInterlockResolutionAsync(
                    key.EquipmentId, key.ParameterId, key, ct);
            }
            finally { gate.Release(); }
        }

        TryGrantRunPermitIfSafe();
    }

    /// <summary>
    /// Re-evaluates one generation-fenced, fully delivered PLC poll without duplicating telemetry/alarm rows.
    /// The worker supplies a predicate that remains true only while no newer poll has started. Physical release
    /// retry is allowed only inside this fresh observation path; the persistence supervisor never releases from
    /// a previously cached value.
    /// </summary>
    internal async Task<bool> EvaluateCompletedPollSnapshotAsync(
        string equipmentId,
        IReadOnlyCollection<FdcTagSample> samples,
        Func<bool> isSnapshotCurrent,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentId);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(isSnapshotCurrent);
        EnsureRuntimeOperational();

        foreach (var sample in samples)
        {
            if (!isSnapshotCurrent())
                return false;

            var key = RuntimeKey(equipmentId, sample.ParameterId);
            var gate = _keyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Recheck under the same per-parameter serialization gate used by live callbacks. A completed
                // newer callback or an already-started next poll invalidates this release candidate.
                if (!isSnapshotCurrent())
                    return false;

                if (sample.Quality != FdcSampleQuality.Good)
                {
                    if (_interlockService!.IsInterlockParameterRuntime(equipmentId, sample.ParameterId))
                    {
                        var failure = new FdcInterlockRuntimeUnavailableException(
                            $"Interlock input '{equipmentId}/{sample.ParameterId}' quality is '{sample.Quality}' "
                            + "in a completed PLC poll; run permit is denied.");
                        FailRuntime(failure);
                        throw failure;
                    }

                    continue;
                }

                await EvaluateInterlockAsync(
                    equipmentId, sample.ParameterId, sample.Value, key, ct);
                await RetryPendingInterlockResolutionAsync(
                    equipmentId, sample.ParameterId, key, ct);
            }
            catch (FdcInterlockRuntimeUnavailableException ex)
            {
                FailRuntime(ex);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }

        TryGrantRunPermitIfSafe();
        return isSnapshotCurrent();
    }

    private async Task ProcessSampleAsync(
        string equipmentId,
        FdcTagSample sample,
        bool requireOperationalRuntime,
        bool markInitialSnapshot,
        CancellationToken ct)
    {
        if (requireOperationalRuntime)
            EnsureRuntimeOperational();
        else
            EnsureRuntimePreparedForInitialSnapshot();

        var key = RuntimeKey(equipmentId, sample.ParameterId);
        var gate = _keyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        FdcInterlockRuntimeUnavailableException? qualityFailure = null;

        if (sample.Quality != FdcSampleQuality.Good && _interlockService is not null)
        {
            bool isInterlockParameter;
            try
            {
                isInterlockParameter = _interlockService.IsInterlockParameterRuntime(equipmentId, sample.ParameterId);
            }
            catch (FdcInterlockRuntimeUnavailableException ex)
            {
                FailRuntime(ex);
                throw;
            }

            if (isInterlockParameter)
            {
                // Bad/Disconnected payload의 fallback 숫자는 규칙 값으로 평가하지 않는다. 다만 인터락 입력을
                // 관찰할 수 없다는 사실 자체로 permit을 DB 접근 전에 즉시 철회한다.
                qualityFailure = new FdcInterlockRuntimeUnavailableException(
                    $"Interlock input '{equipmentId}/{sample.ParameterId}' quality is '{sample.Quality}'; "
                    + "run permit is denied until an explicit runtime re-initialization and Good snapshot.");
                FailRuntime(qualityFailure);
            }
        }

        // 품질이 Good일 때만 인터락 평가/해제를 수행한다. 연결 끊김이나 변환 불가 payload의 fallback 0은
        // 저값 규칙을 거짓 발동시키거나 열린 effect를 거짓 해제할 수 없다.
        if (sample.Quality == FdcSampleQuality.Good && _interlockService is not null)
        {
            await gate.WaitAsync(ct);
            try
            {
                // 규칙 평가는 메모리 전용이며 action의 ack/readback까지 여기서 await한다.
                // history/telemetry DB 작업은 이 결정 뒤에만 실행한다.
                await EvaluateInterlockAsync(equipmentId, sample.ParameterId, sample.Value, key, ct);
                await RetryPendingInterlockResolutionAsync(equipmentId, sample.ParameterId, key, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        var quality = sample.Quality.ToString();
        var recorded = await _dataService.RecordDataAsync(
            collectId: Guid.NewGuid().ToString("N"),
            equipmentId: equipmentId,
            parameterId: sample.ParameterId,
            value: sample.Value,
            quality: quality,
            ct: ct);

        if (qualityFailure is not null)
            throw qualityFailure;

        if (markInitialSnapshot)
        {
            lock (_runtimeStateGate)
                _pendingInitialSnapshots.Remove(key);
        }

        if (recorded.IsFailure) return;

        // §17.5 nexames_fdc_collection_rate 적응 — 적재 성공 1건 계측 (대시보드가 기대 대비 수집률 산정)
        NexaMesMetrics.FdcCollected.Add(1,
            new KeyValuePair<string, object?>("equipmentId", equipmentId),
            new KeyValuePair<string, object?>("quality", quality));

        if (sample.Quality != FdcSampleQuality.Good || _alarmService is null) return;

        await gate.WaitAsync(ct);
        try
        {
            await RestoreAlarmStateAsync(equipmentId, sample.ParameterId, key, ct);
            await EvaluateAlarmAsync(equipmentId, sample.ParameterId, sample.Value, key, ct);
        }
        finally { gate.Release(); }
    }

    private async Task EvaluateInterlockAsync(
        string equipmentId,
        string tagName,
        decimal value,
        FdcRuntimeKey key,
        CancellationToken ct)
    {
        IReadOnlyList<InterlockResult> matches;
        try
        {
            matches = _interlockService!.EvaluateRuntime(equipmentId, tagName, value);
        }
        catch (FdcInterlockRuntimeUnavailableException ex)
        {
            FailRuntime(ex);
            throw;
        }

        _activeInterlocks.TryGetValue(key, out var activeEpisodes);
        if (matches.Count > 0)
        {
            // A confirmed interlock effect and an operational monitoring runtime are different states.
            // Keep the PLC/session supervisor alive, but do not publish automatic-run permission while
            // any effect is active or being reasserted.
            HoldRunPermit();
            activeEpisodes ??= _activeInterlocks.GetOrAdd(key, _ => new List<ActiveInterlockEpisode>());
            foreach (var interlock in matches)
            {
                if (string.IsNullOrWhiteSpace(interlock.RuleId))
                    throw new FdcInterlockRuntimeUnavailableException(
                        $"A matching interlock for '{equipmentId}/{tagName}' has no rule ID.");

                // Episode는 parameter가 아니라 rule별로 유지한다. 같은 입력에서 Warning과 STOP이 동시에
                // match해도 한 action이 다른 action을 마스킹하지 않는다.
                var existing = activeEpisodes
                    .Where(episode => string.Equals(
                        episode.Result.RuleId, interlock.RuleId, StringComparison.Ordinal))
                    .ToArray();
                if (existing.Length > 0)
                {
                    foreach (var active in existing)
                    {
                        if (active.PendingRelease is not null)
                        {
                            // The process condition violated again before release was confirmed. Fence the
                            // pending release locally and re-confirm the original EffectId's physical action.
                            active.ClearPendingRelease();
                            await ApplyActionAsync(
                                equipmentId, tagName, active,
                                isRecovery: false, reconcile: true, ct);
                        }
                        await RecordPendingInterlockAsync(equipmentId, tagName, active, ct);
                    }
                    continue;
                }

                var episode = new ActiveInterlockEpisode(
                    Guid.NewGuid().ToString("N"), value, interlock, DateTime.UtcNow, historyPending: true);
                AddActiveEpisode(key, episode);

                // Prepared 기록은 action 이전에 최선 시도한다. DB 장애가 물리 STOP을 억제해서는 안 되므로
                // 실패해도 같은 EffectId로 action을 계속 수행하고 supervisor가 durable retry를 이어간다.
                await RecordPendingInterlockAsync(equipmentId, tagName, episode, ct);

                // action은 프로젝트 adapter가 EffectId 멱등 키로 실행하고 ack+readback을 모두 확인해야 한다.
                // 실패해도 감지 evidence는 finally에서 보존하며 runtime은 fail-closed로 전환된다.
                try
                {
                    await ApplyActionAsync(equipmentId, tagName, episode, isRecovery: false, reconcile: false, ct);
                    RaiseInterlockTriggered(equipmentId, tagName, episode, isRecovery: false);
                }
                finally
                {
                    await RecordPendingInterlockAsync(equipmentId, tagName, episode, ct);
                }
            }
        }

        if (activeEpisodes is null)
            return;

        var matchingRuleIds = matches
            .Select(static result => result.RuleId!)
            .ToHashSet(StringComparer.Ordinal);
        var resolvedEpisodes = activeEpisodes
            .Where(episode => episode.Result.RuleId is not null
                              && !matchingRuleIds.Contains(episode.Result.RuleId))
            .ToArray();
        foreach (var episode in resolvedEpisodes)
        {
            var normalizedAt = DateTime.UtcNow;
            if (episode.ActionConfirmedAt is { } actionConfirmedAt && normalizedAt < actionConfirmedAt)
                normalizedAt = actionConfirmedAt;
            episode.ObserveNormalizedCondition(value, normalizedAt, isRecovery: false);
            await TryAdvancePendingReleaseAsync(
                equipmentId, tagName, key, activeEpisodes, episode, ct);
        }

        if (activeEpisodes.Count == 0)
            _activeInterlocks.TryRemove(key, out _);
    }

    private async Task RecordPendingInterlockAsync(
        string equipmentId,
        string parameterId,
        ActiveInterlockEpisode episode,
        CancellationToken ct)
    {
        if (!episode.HistoryPending && !episode.AppliedPersistencePending) return;
        if (!_interlockService!.IsHistoryPersistenceConfigured)
        {
            // 경량(no-history) 구성은 의도적인 비영속 모드다. 매 샘플마다 동일 validation failure를 반복하지 않는다.
            episode.HistoryPending = false;
            episode.MarkAppliedPersisted();
            return;
        }

        try
        {
            if (episode.HistoryPending)
            {
                var recorded = await _interlockService.RecordTriggerAsync(
                    episode.EffectId,
                    equipmentId,
                    parameterId,
                    episode.TriggerValue,
                    episode.Result,
                    episode.TriggeredAt,
                    ct);
                if (recorded.IsSuccess)
                    episode.HistoryPending = false;
            }

            // Apply may have completed while the Prepared INSERT was unavailable. Once that row
            // becomes durable, persist the same acknowledgement/readback instead of leaving the
            // lifecycle permanently at Prepared. A false CAS result remains pending for the worker
            // supervisor; it is not silently treated as evidence.
            if (!episode.HistoryPending
                && episode.AppliedPersistencePending
                && episode.ActionResult is { IsConfirmed: true } actionResult
                && episode.ActionConfirmedAt is { } actionConfirmedAt
                && await _interlockService.MarkAppliedAsync(
                    episode.EffectId, actionResult, actionConfirmedAt, ct))
            {
                episode.MarkAppliedPersisted();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DB 장애는 최초 신호를 되돌리거나 같은 episode의 action/event를 재발행하지 않는다.
            // 각 pending flag를 유지해 다음 샘플 또는 supervisor에서 같은 EffectId로 재시도한다.
        }
    }

    /// <summary>
    /// Advances a normalized episode independently of PLC value-change callbacks. Manual reset or a
    /// transient action-adapter failure can therefore converge from the worker supervisor while the
    /// input remains steadily normal. The original EffectId remains the adapter idempotency key.
    /// </summary>
    private async Task<bool> TryAdvancePendingReleaseAsync(
        string equipmentId,
        string parameterId,
        FdcRuntimeKey key,
        List<ActiveInterlockEpisode> activeEpisodes,
        ActiveInterlockEpisode episode,
        CancellationToken ct)
    {
        var pending = episode.PendingRelease;
        if (pending is null)
            return false;

        HoldRunPermit();
        await RecordPendingInterlockAsync(equipmentId, parameterId, episode, ct);
        if (episode.HistoryPending || episode.AppliedPersistencePending)
            return false;

        await PersistPendingReleaseEvidenceAsync(episode, ct);
        if (!pending.ConditionPersisted)
            return false;

        var release = await ReleaseActionAsync(
            equipmentId,
            parameterId,
            episode,
            pending.Value,
            pending.IsRecovery,
            ct);
        if (!release.IsConfirmed)
        {
            pending.ObserveReleasePending(release.Detail);
            await PersistPendingReleaseEvidenceAsync(episode, ct);
            return false;
        }

        var releaseConfirmedAt = DateTime.UtcNow;
        if (releaseConfirmedAt < pending.NormalizedAt)
            releaseConfirmedAt = pending.NormalizedAt;

        EnqueuePendingResolution(
            key,
            new PendingInterlockResolution(
                episode,
                pending.Value,
                pending.NormalizedAt,
                releaseConfirmedAt,
                release));

        // Physical release is already confirmed. DB terminal persistence is a separate EffectId ledger;
        // keeping it in the physical active set would mask a new violation while the DB is unavailable.
        RemoveActiveEpisode(key, activeEpisodes, episode);
        await RetryPendingInterlockResolutionAsync(equipmentId, parameterId, key, ct);
        TryGrantRunPermitIfSafe();
        return true;
    }

    private async Task PersistPendingReleaseEvidenceAsync(
        ActiveInterlockEpisode episode,
        CancellationToken ct)
    {
        var pending = episode.PendingRelease;
        if (pending is null)
            return;

        if (!_interlockService!.IsHistoryPersistenceConfigured)
        {
            pending.MarkConditionPersisted();
            pending.MarkReleasePendingPersisted();
            return;
        }

        if (!pending.ConditionPersisted)
        {
            try
            {
                if (await _interlockService.MarkConditionNormalizedAsync(
                        episode.EffectId, pending.NormalizedAt, pending.Value, ct))
                    pending.MarkConditionPersisted();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return;
            }
        }

        if (!pending.ConditionPersisted
            || !pending.ReleasePendingObserved
            || pending.ReleasePendingPersisted)
            return;

        try
        {
            if (await _interlockService.MarkReleasePendingAsync(
                    episode.EffectId, pending.ReleasePendingDetail, ct))
                pending.MarkReleasePendingPersisted();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The in-memory release intent remains authoritative and the DB-only supervisor retries it.
        }
    }

    private async Task RetryPendingInterlockResolutionAsync(
        string equipmentId,
        string parameterId,
        FdcRuntimeKey key,
        CancellationToken ct)
    {
        if (!_pendingInterlockResolutions.TryGetValue(key, out var pendingQueue)
            || pendingQueue.Count == 0)
            return;

        // 이력 저장소를 의도적으로 생략한 경량 구성에는 durable 재시도 대상이 없다.
        if (!_interlockService!.IsHistoryPersistenceConfigured)
        {
            while (pendingQueue.Count > 0)
            {
                var pending = DequeuePendingResolution(pendingQueue);
                InterlockResolved?.Invoke(this,
                    new FdcInterlockResolvedEventArgs(
                        pending.Episode.EffectId, pending.Episode.Result.RuleId,
                        equipmentId, parameterId, pending.Value, pending.ReleaseConfirmedAt));
            }
            _pendingInterlockResolutions.TryRemove(key, out _);
            return;
        }

        try
        {
            while (pendingQueue.Count > 0)
            {
                var pending = pendingQueue.Peek();
                // trigger INSERT가 실패한 채 정상 복귀했더라도 trigger→resolve 증거 순서를 보존한다.
                // 같은 ActiveInterlockEpisode 객체를 보관하므로 EffectId/최초 값/규칙/시각이 바뀌지 않는다.
                await RecordPendingInterlockAsync(equipmentId, parameterId, pending.Episode, ct);
                if (pending.Episode.HistoryPending || pending.Episode.AppliedPersistencePending) return;

                var resolved = await _interlockService.ResolveEffectAsync(
                    pending.Episode.EffectId,
                    equipmentId,
                    parameterId,
                    pending.Value,
                    pending.ReleaseConfirmedAt,
                    pending.ReleaseResult,
                    ct);
                // 0건은 성공이 아니다. 아직 trigger 행이 보이지 않거나 다른 장애가 있었을 수 있으므로
                // 다음 Good 샘플에서 같은 EffectId로 다시 확인한다.
                if (resolved == 0) return;
                DequeuePendingResolution(pendingQueue);
                InterlockResolved?.Invoke(this,
                    new FdcInterlockResolvedEventArgs(
                        pending.Episode.EffectId, pending.Episode.Result.RuleId,
                        equipmentId, parameterId, pending.Value, pending.ReleaseConfirmedAt));
            }

            _pendingInterlockResolutions.TryRemove(key, out _);
            TryGrantRunPermitIfSafe();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 정상 복귀 신호는 이미 1회 발생했다. durable 해제만 다음 Good 샘플에서 재시도한다.
        }
    }

    private async Task EvaluateAlarmAsync(
        string equipmentId,
        string tagName,
        decimal value,
        FdcRuntimeKey key,
        CancellationToken ct)
    {
        var alarms = await _alarmService!.EvaluateAsync(equipmentId, tagName, value, ct);
        var hits = alarms
            .GroupBy(static alarm => alarm.AlarmConfigId, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(alarm => SeverityRank(alarm.AlarmLevel))
                .First())
            .ToDictionary(static alarm => alarm.AlarmConfigId, StringComparer.Ordinal);
        var active = _activeAlarms.GetOrAdd(
            key, static _ => new Dictionary<string, string>(StringComparer.Ordinal));

        // 한 parameter에 Warning과 Critical이 함께 열려 있어도 현재 정상화된 config만 닫는다.
        foreach (var alarmConfigId in active.Keys
                     .Where(configId => !hits.ContainsKey(configId))
                     .ToArray())
        {
            await _alarmService.ClearActiveAsync(equipmentId, tagName, alarmConfigId, ct);
            active.Remove(alarmConfigId);
            AlarmCleared?.Invoke(this,
                new FdcAlarmClearedEventArgs(equipmentId, tagName, value, alarmConfigId));
        }

        // 낮은 심각도부터 게시하면 같은 샘플의 최종 UI 상태가 가장 높은 심각도로 끝난다.
        foreach (var alarm in hits.Values
                     .OrderBy(alarm => SeverityRank(alarm.AlarmLevel))
                     .ThenBy(static alarm => alarm.AlarmConfigId, StringComparer.Ordinal))
        {
            if (active.ContainsKey(alarm.AlarmConfigId))
                continue;

            try
            {
                await _alarmService.RecordAsync(equipmentId, tagName, value, alarm, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 기록 실패 시 해당 config를 활성화하지 않는다. 다음 샘플은 DB open state를 다시 읽고
                // 같은 config를 재평가한다.
                _loadedAlarmStates.TryRemove(key, out _);
                return;
            }
            active[alarm.AlarmConfigId] = alarm.AlarmLevel;
            AlarmRaised?.Invoke(this, new FdcAlarmRaisedEventArgs(equipmentId, tagName, value, alarm));
        }

        if (active.Count == 0)
            _activeAlarms.TryRemove(key, out _);
    }

    private async Task ApplyActionAsync(
        string equipmentId,
        string parameterId,
        ActiveInterlockEpisode episode,
        bool isRecovery,
        bool reconcile,
        CancellationToken ct)
    {
        if (_actionPort is null)
        {
            var failure = new FdcInterlockRuntimeUnavailableException(
                "A project-owned IFdcInterlockActionPort is required; run permit is denied.");
            FailRuntime(failure);
            throw failure;
        }

        FdcInterlockActionResult result;
        try
        {
            result = await AwaitActionPortAsync(
                token => (reconcile ? _actionPort.ReconcileAsync(
                    new FdcInterlockActionRequest(
                        episode.EffectId, episode.Result.RuleId!, equipmentId, parameterId,
                        episode.TriggerValue, episode.Result.Action, isRecovery,
                        episode.TriggeredAt, episode.Result.Message, GetRuntimeAuthority()), token) : _actionPort.ApplyAsync(
                    new FdcInterlockActionRequest(
                        episode.EffectId,
                        episode.Result.RuleId!,
                        equipmentId,
                        parameterId,
                        episode.TriggerValue,
                        episode.Result.Action,
                        isRecovery,
                        episode.TriggeredAt,
                        episode.Result.Message,
                        GetRuntimeAuthority()),
                    token)),
                ct);
        }
        catch (OperationCanceledException ex)
        {
            FailRuntime(ex);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new FdcInterlockActionFailedException(
                episode.EffectId,
                $"Interlock action '{episode.Result.Action}' threw before acknowledgement/readback; run permit is denied.",
                ex);
            FailRuntime(failure);
            try { await _interlockService!.MarkActionErrorAsync(episode.EffectId, ex.Message, ct); }
            catch (Exception persistenceError) when (persistenceError is not OperationCanceledException) { }
            throw failure;
        }

        if (result is null || !result.IsConfirmed)
        {
            var failure = new FdcInterlockActionFailedException(
                episode.EffectId,
                $"Interlock action '{episode.Result.Action}' was not confirmed by acknowledgement and readback: " +
                $"{result?.Detail ?? "no result"}.");
            FailRuntime(failure);
            try
            {
                await _interlockService!.MarkActionErrorAsync(
                    episode.EffectId, result?.Detail ?? "Action acknowledgement/readback was not confirmed.", ct);
            }
            catch (Exception persistenceError) when (persistenceError is not OperationCanceledException) { }
            throw failure;
        }

        var confirmedAt = DateTime.UtcNow;
        episode.MarkActionApplied(result, confirmedAt);
        try
        {
            if (await _interlockService!.MarkAppliedAsync(episode.EffectId, result, confirmedAt, ct))
                episode.MarkAppliedPersisted();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The physical action remains confirmed. Its durable state is retried independently
            // from the Prepared insert so a recovered row cannot remain permanently Prepared.
        }
    }

    private async Task<FdcInterlockReleaseResult> ReleaseActionAsync(
        string equipmentId, string parameterId, ActiveInterlockEpisode episode,
        decimal normalizedValue, bool isRecovery, CancellationToken ct)
    {
        if (_actionPort is null)
        {
            HoldRunPermit();
            return new FdcInterlockReleaseResult(false, false, true, null, "Action adapter is unavailable.");
        }

        try
        {
            var result = await AwaitActionPortAsync(token => _actionPort.ReleaseAsync(
                new FdcInterlockReleaseRequest(
                    episode.EffectId, episode.Result.RuleId!, equipmentId, parameterId,
                    episode.Result.Action, normalizedValue, FdcInterlockResetPolicy.ManualRequired,
                    isRecovery, GetRuntimeAuthority()), token), ct);
            if (result is null || !result.IsConfirmed) HoldRunPermit();
            return result ?? new FdcInterlockReleaseResult(false, false, true, null, "No release result.");
        }
        catch (OperationCanceledException ex)
        {
            // Cancellation leaves release confirmation unknown. Revoke both admission and runtime health so
            // the worker closes its sessions; a restart must reconcile the durable EffectId before any grant.
            FailRuntime(ex);
            throw;
        }
        catch (TimeoutException ex)
        {
            // The caller no longer knows whether a release that ignored cancellation can complete late.
            // Without a returned controller fence/readback this is a terminal runtime fault, not an
            // ordinary manual-reset wait.
            var failure = new FdcInterlockActionFailedException(
                episode.EffectId,
                $"Interlock release for action '{episode.Result.Action}' timed out with an unknown physical outcome; " +
                "full runtime reconciliation is required.",
                ex);
            FailRuntime(failure);
            throw failure;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HoldRunPermit();
            return new FdcInterlockReleaseResult(false, false, true, null, ex.Message);
        }
    }

    private async Task<T> AwaitActionPortAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        using var adapterCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await operation(adapterCancellation.Token).WaitAsync(_actionTimeout, ct);
        }
        catch (TimeoutException)
        {
            // WaitAsync bounds even an adapter that ignores cancellation. EffectId is the mandatory idempotency
            // key if the adapter finishes after this caller has already failed closed.
            adapterCancellation.Cancel();
            throw;
        }
    }

    private void RaiseInterlockTriggered(
        string equipmentId,
        string parameterId,
        ActiveInterlockEpisode episode,
        bool isRecovery)
    {
        if (episode.ActionResult is null)
            throw new InvalidOperationException("An interlock effect cannot be published before action confirmation.");

        InterlockTriggered?.Invoke(this,
            new FdcInterlockTriggeredEventArgs(
                episode.EffectId,
                equipmentId,
                parameterId,
                episode.TriggerValue,
                episode.Result,
                episode.ActionResult,
                isRecovery));
    }

    private void AddActiveEpisode(FdcRuntimeKey key, ActiveInterlockEpisode episode)
    {
        var episodes = _activeInterlocks.GetOrAdd(key, _ => new List<ActiveInterlockEpisode>());
        lock (_runtimeStateGate)
        {
            if (episodes.Any(candidate => candidate.EffectId == episode.EffectId))
                return;

            _runPermit = 0;
            episodes.Add(episode);
            _activeEffectCount++;
        }
    }

    private void RemoveActiveEpisode(
        FdcRuntimeKey key,
        List<ActiveInterlockEpisode> episodes,
        ActiveInterlockEpisode episode)
    {
        lock (_runtimeStateGate)
        {
            if (!episodes.Remove(episode))
                return;
            _activeEffectCount--;
            if (_activeEffectCount < 0)
                throw new InvalidOperationException("FDC active-effect count became negative.");
        }

        if (episodes.Count == 0)
            _activeInterlocks.TryRemove(key, out _);
    }

    private void EnqueuePendingResolution(
        FdcRuntimeKey key,
        PendingInterlockResolution pending)
    {
        var queue = _pendingInterlockResolutions.GetOrAdd(
            key, _ => new Queue<PendingInterlockResolution>());
        lock (_runtimeStateGate)
        {
            if (queue.Any(candidate => candidate.Episode.EffectId == pending.Episode.EffectId))
                return;
            _runPermit = 0;
            queue.Enqueue(pending);
            _pendingResolutionCount++;
        }
    }

    private PendingInterlockResolution DequeuePendingResolution(
        Queue<PendingInterlockResolution> queue)
    {
        lock (_runtimeStateGate)
        {
            var pending = queue.Dequeue();
            _pendingResolutionCount--;
            if (_pendingResolutionCount < 0)
                throw new InvalidOperationException("FDC pending-resolution count became negative.");
            return pending;
        }
    }

    private void EnsureRuntimeOperational()
    {
        if (_interlockService is null) return;
        if (Volatile.Read(ref _runtimeOperational) == 1 && _interlockService.IsRuntimeInitialized) return;

        var failure = new FdcInterlockRuntimeUnavailableException(
            "FDC interlock monitoring runtime is not initialized or was invalidated; run permit is denied.");
        FailRuntime(failure);
        throw failure;
    }

    private void EnsureRuntimePreparedForInitialSnapshot()
    {
        if (_interlockService is null) return;

        lock (_runtimeStateGate)
        {
            if (_preparedRuntimeRevision is not null
                && _interlockService.IsRuntimeCurrent(_preparedRuntimeRevision.Value))
                return;
        }

        var failure = new FdcInterlockRuntimeUnavailableException(
            "FDC interlock runtime is not prepared for initial snapshot evaluation; run permit is denied.");
        FailRuntime(failure);
        throw failure;
    }

    private void OnInterlockRuntimeInvalidated()
    {
        lock (_runtimeStateGate)
        {
            _preparedRuntimeRevision = null;
        }
        FailRuntime(new FdcInterlockRuntimeUnavailableException(
            "FDC interlock rule/topology snapshot was invalidated while the runtime was active."));
    }

    private void HoldRunPermit()
    {
        if (_interlockService is null)
            return;

        lock (_runtimeStateGate)
            _runPermit = 0;
    }

    private FdcRuntimeAuthority? GetRuntimeAuthority()
    {
        lock (_runtimeStateGate)
            return _runtimeAuthority;
    }

    private void TryGrantRunPermitIfSafe()
    {
        if (_interlockService is null)
            return;

        lock (_runtimeStateGate)
        {
            if (_runtimeOperational == 1
                && _preparedRuntimeRevision is { } revision
                && _interlockService.IsRuntimeCurrent(revision)
                && _pendingInitialSnapshots.Count == 0
                && _activeEffectCount == 0
                && _pendingResolutionCount == 0)
            {
                _runPermit = 1;
            }
        }
    }

    private void FailRuntime(Exception? cause = null)
    {
        if (_interlockService is null)
            return;

        bool notify;
        Exception fault;
        lock (_runtimeStateGate)
        {
            notify = _runtimeOperational == 1;
            _runtimeFault ??= cause ?? new FdcInterlockRuntimeUnavailableException(
                "FDC interlock monitoring runtime entered a fail-closed terminal state.");
            fault = _runtimeFault;
            _runtimeOperational = 0;
            _runPermit = 0;
            _preparedRuntimeRevision = null;
        }

        if (notify)
            RuntimeFaulted?.Invoke(fault);
    }

    private static FdcRuntimeKey RuntimeKey(string equipmentId, string parameterId) =>
        new(equipmentId, parameterId);

    private async Task RestoreAlarmStateAsync(
        string equipmentId,
        string parameterId,
        FdcRuntimeKey key,
        CancellationToken ct)
    {
        if (_loadedAlarmStates.ContainsKey(key)) return;

        var open = await _alarmService!.GetOpenByParameterAsync(equipmentId, parameterId, ct);
        if (open.Count > 0)
            _activeAlarms[key] = ToActiveAlarmMap(open);
        _loadedAlarmStates.TryAdd(key, 0);
    }

    private async Task PreloadAlarmStateAsync(
        IReadOnlyCollection<FdcInterlockTopology> topology,
        CancellationToken ct)
    {
        if (_alarmService is null) return;

        foreach (var equipment in topology
                     .GroupBy(item => item.EquipmentId, StringComparer.Ordinal)
                     .Select(group => new
                     {
                         EquipmentId = group.Key,
                         ParameterIds = group.SelectMany(item => item.ParameterIds)
                             .Distinct(StringComparer.Ordinal)
                             .ToArray()
                     }))
        {
            var open = await _alarmService.GetOpenByEquipmentAsync(equipment.EquipmentId, ct);
            foreach (var parameterGroup in open.GroupBy(history => history.ParameterId, StringComparer.Ordinal))
            {
                _activeAlarms[RuntimeKey(equipment.EquipmentId, parameterGroup.Key)] =
                    ToActiveAlarmMap(parameterGroup);
            }

            // 모든 topology key를 loaded로 표시해 첫 샘플의 parameter-scoped DB point-read를 제거한다.
            foreach (var parameterId in equipment.ParameterIds)
                _loadedAlarmStates[RuntimeKey(equipment.EquipmentId, parameterId)] = 0;
        }
    }

    /// <summary>알람 심각도 순위(Critical &gt; Warning &gt; 기타). 심각도 상승 통지 판단에 사용.</summary>
    private static int SeverityRank(string level) => level switch
    {
        "Critical" => 2,
        "Warning"  => 1,
        _          => 0,
    };

    private static Dictionary<string, string> ToActiveAlarmMap(
        IEnumerable<FdcAlarmHistory> histories) =>
        histories
            .GroupBy(static history => history.AlarmConfigId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(history => history.AlarmLevel)
                    .OrderByDescending(SeverityRank)
                    .First(),
                StringComparer.Ordinal);
}

internal readonly record struct FdcRuntimeKey(string EquipmentId, string ParameterId);

internal sealed class ActiveInterlockEpisode
{
    public ActiveInterlockEpisode(
        string effectId,
        decimal triggerValue,
        InterlockResult result,
        DateTime triggeredAt,
        bool historyPending)
    {
        EffectId = effectId;
        TriggerValue = triggerValue;
        Result = result;
        TriggeredAt = triggeredAt;
        HistoryPending = historyPending;
    }

    public string EffectId { get; }
    public decimal TriggerValue { get; }
    public InterlockResult Result { get; }
    public DateTime TriggeredAt { get; }
    public bool HistoryPending { get; set; }
    public FdcInterlockActionResult? ActionResult { get; private set; }
    public DateTime? ActionConfirmedAt { get; private set; }
    public bool AppliedPersistencePending { get; private set; }
    public PendingInterlockRelease? PendingRelease { get; private set; }

    public void MarkActionApplied(FdcInterlockActionResult result, DateTime confirmedAt)
    {
        ActionResult = result;
        ActionConfirmedAt = confirmedAt;
        AppliedPersistencePending = true;
    }

    public void MarkAppliedPersisted() => AppliedPersistencePending = false;

    public void ObserveNormalizedCondition(decimal value, DateTime normalizedAt, bool isRecovery)
        => PendingRelease ??= new PendingInterlockRelease(value, normalizedAt, isRecovery);

    public void ClearPendingRelease() => PendingRelease = null;
}

internal sealed class PendingInterlockRelease
{
    public PendingInterlockRelease(decimal value, DateTime normalizedAt, bool isRecovery)
    {
        Value = value;
        NormalizedAt = normalizedAt;
        IsRecovery = isRecovery;
    }

    public decimal Value { get; }
    public DateTime NormalizedAt { get; }
    public bool IsRecovery { get; }
    public bool ConditionPersisted { get; private set; }
    public bool ReleasePendingObserved { get; private set; }
    public bool ReleasePendingPersisted { get; private set; }
    public string? ReleasePendingDetail { get; private set; }

    public void MarkConditionPersisted() => ConditionPersisted = true;

    public void ObserveReleasePending(string? detail)
    {
        if (!ReleasePendingObserved
            || !string.Equals(ReleasePendingDetail, detail, StringComparison.Ordinal))
            ReleasePendingPersisted = false;
        ReleasePendingObserved = true;
        ReleasePendingDetail = detail;
    }

    public void MarkReleasePendingPersisted()
    {
        if (ReleasePendingObserved)
            ReleasePendingPersisted = true;
    }
}

internal sealed record PendingInterlockResolution(
    ActiveInterlockEpisode Episode,
    decimal Value,
    DateTime ConditionNormalizedAt,
    DateTime ReleaseConfirmedAt,
    FdcInterlockReleaseResult ReleaseResult);

/// <summary>인터락 규칙 발동 이벤트 인자 (§10.4.2).</summary>
public sealed record FdcInterlockTriggeredEventArgs(
    string EffectId,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    InterlockResult Result,
    FdcInterlockActionResult ActionResult,
    bool IsRecovery);

/// <summary>인터락 해제(정상 복귀) 이벤트 인자 (§10.4.2).</summary>
public sealed record FdcInterlockResolvedEventArgs(
    string EffectId,
    string? RuleId,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    DateTime ResolvedAt);

/// <summary>임계치 알람 발생 이벤트 인자 (§10.4.1).</summary>
public sealed record FdcAlarmRaisedEventArgs(
    string EquipmentId,
    string ParameterId,
    decimal Value,
    AlarmResult Alarm);

/// <summary>알람 해제(정상 복귀) 이벤트 인자 (§10.4.1).</summary>
public sealed record FdcAlarmClearedEventArgs(
    string EquipmentId,
    string ParameterId,
    decimal Value,
    string AlarmConfigId);

public sealed class FdcInterlockActionFailedException : InvalidOperationException
{
    public FdcInterlockActionFailedException(string effectId, string message)
        : base(message) => EffectId = effectId;

    public FdcInterlockActionFailedException(string effectId, string message, Exception innerException)
        : base(message, innerException) => EffectId = effectId;

    public string EffectId { get; }
}
