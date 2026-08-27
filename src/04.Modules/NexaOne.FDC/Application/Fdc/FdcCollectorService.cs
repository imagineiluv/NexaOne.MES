using System.Collections.Concurrent;
using NexaOne.Common.Telemetry;

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

    // 현재 발동 중인 (설비|파라미터) episode. EffectId는 즉시 신호와 durable 이력 재시도 전 구간에서 고정한다.
    private readonly ConcurrentDictionary<string, ActiveInterlockEpisode> _activeInterlocks = new();
    // 정상 복귀 신호 뒤 DB 해제가 실패한 episode. 다음 Good 샘플에서 이력 해제만 재시도한다.
    private readonly ConcurrentDictionary<string, Queue<PendingInterlockResolution>> _pendingInterlockResolutions = new();
    // durable 이력 조회를 완료한 키. 프로세스 재시작 후 첫 Good 샘플에서 open 상태를 한 번 복원한다.
    private readonly ConcurrentDictionary<string, byte> _loadedInterlockStates = new();
    // 현재 발생 중인 알람의 최고 심각도(레벨) — 심각도 상승(Warning→Critical) 통지 판단용
    private readonly ConcurrentDictionary<string, string> _activeAlarms = new();
    private readonly ConcurrentDictionary<string, byte> _loadedAlarmStates = new();
    // (설비|파라미터) 키별 평가-기록-해제 직렬화 — 동시 태그 이벤트의 발동↔해제 순서 역전 방지
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyGates = new();

    /// <summary>인터락 규칙이 최초 발동했을 때 DB 기록보다 먼저 한 번 발생한다. 액션 해석과 실제 설비 제어는
    /// 프로젝트별 플러그인/소비자가 담당하며, 공통 FDC의 버스 알림 워커는 물리 안전 동작이 아니다 (§10.4.2).</summary>
    public event EventHandler<FdcInterlockTriggeredEventArgs>? InterlockTriggered;

    /// <summary>발동했던 인터락이 정상 범위 복귀로 해제됐을 때 발생한다 (§10.4.2).</summary>
    public event EventHandler<FdcInterlockResolvedEventArgs>? InterlockResolved;

    /// <summary>임계치 알람이 발생했을 때 발생한다 (§10.4.1). 후속 알림은 호스트 구독자가 처리한다.</summary>
    public event EventHandler<FdcAlarmRaisedEventArgs>? AlarmRaised;

    /// <summary>발생했던 알람이 정상 범위 복귀로 해제됐을 때 발생한다 (§10.4.1).</summary>
    public event EventHandler<FdcAlarmClearedEventArgs>? AlarmCleared;

    public FdcCollectorService(
        FdcDataService dataService,
        FdcInterlockService? interlockService = null,
        FdcAlarmService? alarmService = null)
    {
        _dataService = dataService;
        _interlockService = interlockService;
        _alarmService = alarmService;
    }

    /// <summary>태그 변경 1건을 수집 데이터로 적재하고, 인터락 규칙을 평가한다.
    /// 파라미터 미정의·검증 실패는 삼킨다(폭주 방지) — 이 경우 인터락 평가도 건너뛴다.</summary>
    public async Task OnTagChangeAsync(string equipmentId, FdcTagSample sample, CancellationToken ct = default)
    {
        var quality = sample.Quality.ToString();

        var recorded = await _dataService.RecordDataAsync(
            collectId: Guid.NewGuid().ToString("N"),
            equipmentId: equipmentId,
            parameterId: sample.ParameterId,
            value: sample.Value,
            quality: quality,
            ct: ct);

        if (recorded.IsFailure) return;   // 미정의 파라미터·검증 실패 — 인터락 평가 생략

        // §17.5 nexames_fdc_collection_rate 적응 — 적재 성공 1건 계측 (대시보드가 기대 대비 수집률 산정)
        NexaMesMetrics.FdcCollected.Add(1,
            new KeyValuePair<string, object?>("equipmentId", equipmentId),
            new KeyValuePair<string, object?>("quality", quality));

        if (_interlockService is null && _alarmService is null) return;

        // 품질이 Good이 아니면 평가·해제하지 않는다. 연결 끊김이나 변환 불가 payload의 Bad 표본이
        // fallback 0으로 저장되더라도 활성 인터락/알람을 거짓 해제하거나 저값 규칙을 발동시키지 않는다.
        if (sample.Quality != FdcSampleQuality.Good) return;

        // 같은 (설비|태그) 이벤트의 동시 처리를 직렬화 — TryAdd 후 RecordTrigger(INSERT)와
        // 정상 복귀 시 ResolveActive(미해제 행 해제)의 순서 역전(발동↔해제 비대칭)을 막는다.
        var key = $"{equipmentId}|{sample.ParameterId}";
        var gate = _keyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_interlockService is not null)
            {
                await RetryPendingInterlockResolutionAsync(equipmentId, sample.ParameterId, key, ct);
                await RestoreInterlockStateAsync(equipmentId, sample.ParameterId, key, ct);
                await EvaluateInterlockAsync(equipmentId, sample.ParameterId, sample.Value, key, ct);
            }
            if (_alarmService is not null)
            {
                await RestoreAlarmStateAsync(equipmentId, sample.ParameterId, key, ct);
                await EvaluateAlarmAsync(equipmentId, sample.ParameterId, sample.Value, key, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EvaluateInterlockAsync(string equipmentId, string tagName, decimal value, string key, CancellationToken ct)
    {
        var interlock = await _interlockService!.EvaluateAsync(equipmentId, tagName, value, ct);

        if (interlock.IsTriggered)
        {
            // 이미 발동 중이면 action/event는 중복하지 않는다. 이전 기록이 실패한 경우에만 최초 episode의
            // EffectId/값/규칙을 그대로 사용해 durable 이력을 재시도한다.
            if (_activeInterlocks.TryGetValue(key, out var active))
            {
                await RecordPendingInterlockAsync(equipmentId, tagName, active, ct);
                return;
            }

            var episode = new ActiveInterlockEpisode(
                Guid.NewGuid().ToString("N"), value, interlock, DateTime.UtcNow, historyPending: true);
            if (!_activeInterlocks.TryAdd(key, episode)) return;

            // 안전 관련 프로젝트 소비자가 DB 상태와 무관하게 첫 신호를 받을 수 있도록 반드시 기록보다 먼저 발생시킨다.
            // 구독자 예외가 나더라도 finally에서 이력 기록은 시도하고, active episode는 유지해 중복 action을 막는다.
            try
            {
                InterlockTriggered?.Invoke(this,
                    new FdcInterlockTriggeredEventArgs(
                        episode.EffectId, equipmentId, tagName, value, interlock));
            }
            finally
            {
                await RecordPendingInterlockAsync(equipmentId, tagName, episode, ct);
            }
        }
        else if (_activeInterlocks.TryRemove(key, out var episode))
        {
            // 정상 복귀도 DB 장애가 실시간 사실 통지를 억제하지 않게 메모리 episode를 먼저 닫고 신호를 낸다.
            // durable 해제 실패는 별도 pending으로 남겨 다음 Good 샘플에서 재시도한다.
            var resolvedAt = DateTime.UtcNow;
            var pendingResolutions = _pendingInterlockResolutions.GetOrAdd(
                key, _ => new Queue<PendingInterlockResolution>());
            pendingResolutions.Enqueue(new PendingInterlockResolution(episode, value, resolvedAt));
            try
            {
                InterlockResolved?.Invoke(this,
                    new FdcInterlockResolvedEventArgs(
                        episode.EffectId, episode.Result.RuleId, equipmentId, tagName, value, resolvedAt));
            }
            finally
            {
                await RetryPendingInterlockResolutionAsync(equipmentId, tagName, key, ct);
            }
        }
    }

    private async Task RecordPendingInterlockAsync(
        string equipmentId,
        string parameterId,
        ActiveInterlockEpisode episode,
        CancellationToken ct)
    {
        if (!episode.HistoryPending) return;
        if (!_interlockService!.IsHistoryPersistenceConfigured)
        {
            // 경량(no-history) 구성은 의도적인 비영속 모드다. 매 샘플마다 동일 validation failure를 반복하지 않는다.
            episode.HistoryPending = false;
            return;
        }

        try
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DB 장애는 최초 신호를 되돌리거나 같은 episode의 action/event를 재발행하지 않는다.
            // HistoryPending을 유지해 다음 위반 샘플에서 동일 EffectId로 기록만 재시도한다.
        }
    }

    private async Task RetryPendingInterlockResolutionAsync(
        string equipmentId,
        string parameterId,
        string key,
        CancellationToken ct)
    {
        if (!_pendingInterlockResolutions.TryGetValue(key, out var pendingQueue)
            || pendingQueue.Count == 0)
            return;

        // 이력 저장소를 의도적으로 생략한 경량 구성에는 durable 재시도 대상이 없다.
        if (!_interlockService!.IsHistoryPersistenceConfigured)
        {
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
                if (pending.Episode.HistoryPending) return;

                var resolved = await _interlockService.ResolveEffectAsync(
                    pending.Episode.EffectId,
                    equipmentId,
                    parameterId,
                    pending.Value,
                    pending.ResolvedAt,
                    ct);
                // 0건은 성공이 아니다. 아직 trigger 행이 보이지 않거나 다른 장애가 있었을 수 있으므로
                // 다음 Good 샘플에서 같은 EffectId로 다시 확인한다.
                if (resolved == 0) return;
                pendingQueue.Dequeue();
            }

            _pendingInterlockResolutions.TryRemove(key, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 정상 복귀 신호는 이미 1회 발생했다. durable 해제만 다음 Good 샘플에서 재시도한다.
        }
    }

    private async Task EvaluateAlarmAsync(string equipmentId, string tagName, decimal value, string key, CancellationToken ct)
    {
        var alarms = await _alarmService!.EvaluateAsync(equipmentId, tagName, value, ct);

        if (alarms.Count > 0)
        {
            // 동시에 여러 레벨이 잡히면 가장 심각한 것을 채택
            var top = alarms.Any(a => a.AlarmLevel == "Critical")
                ? alarms.First(a => a.AlarmLevel == "Critical")
                : alarms[0];

            // 신규 발생 또는 심각도 상승(예: Warning→Critical)만 기록·통지 (동일 레벨 반복은 억제)
            var isNew = !_activeAlarms.TryGetValue(key, out var current);
            if (isNew || SeverityRank(top.AlarmLevel) > SeverityRank(current!))
            {
                try
                {
                    await _alarmService.RecordAsync(equipmentId, tagName, value, top, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 기록 실패 시 활성 레벨을 갱신하지 않는다 — 갱신해 버리면 기록 없는 알람이 이후 동일/하위
                    // 레벨을 거짓 억제한다. 갱신을 미뤄 다음 태그 변화에서 재평가·재기록되도록 한다.
                    _loadedAlarmStates.TryRemove(key, out _);
                    return;
                }
                _activeAlarms[key] = top.AlarmLevel;   // 기록 성공 후에만 활성 레벨 갱신
                AlarmRaised?.Invoke(this, new FdcAlarmRaisedEventArgs(equipmentId, tagName, value, top));
            }
        }
        else if (_activeAlarms.ContainsKey(key))
        {
            await _alarmService.ClearActiveAsync(equipmentId, tagName, ct);
            _activeAlarms.TryRemove(key, out _);
            AlarmCleared?.Invoke(this, new FdcAlarmClearedEventArgs(equipmentId, tagName, value));
        }
    }

    private async Task RestoreInterlockStateAsync(
        string equipmentId,
        string parameterId,
        string key,
        CancellationToken ct)
    {
        if (_loadedInterlockStates.ContainsKey(key)) return;

        var unresolved = await _interlockService!.GetLatestUnresolvedAsync(equipmentId, parameterId, ct);
        if (unresolved is not null)
        {
            _activeInterlocks.TryAdd(key, new ActiveInterlockEpisode(
                unresolved.Id,
                unresolved.TriggerValue,
                InterlockResult.Triggered(
                    unresolved.Action, unresolved.Message, unresolved.RuleId),
                unresolved.TriggeredAt,
                historyPending: false));
        }
        _loadedInterlockStates.TryAdd(key, 0);
    }

    private async Task RestoreAlarmStateAsync(
        string equipmentId,
        string parameterId,
        string key,
        CancellationToken ct)
    {
        if (_loadedAlarmStates.ContainsKey(key)) return;

        var level = await _alarmService!.GetHighestOpenLevelAsync(equipmentId, parameterId, ct);
        if (level is not null)
            _activeAlarms[key] = level;
        _loadedAlarmStates.TryAdd(key, 0);
    }

    /// <summary>알람 심각도 순위(Critical &gt; Warning &gt; 기타). 심각도 상승 통지 판단에 사용.</summary>
    private static int SeverityRank(string level) => level switch
    {
        "Critical" => 2,
        "Warning"  => 1,
        _          => 0,
    };
}

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
}

internal sealed record PendingInterlockResolution(
    ActiveInterlockEpisode Episode,
    decimal Value,
    DateTime ResolvedAt);

/// <summary>인터락 규칙 발동 이벤트 인자 (§10.4.2).</summary>
public sealed record FdcInterlockTriggeredEventArgs(
    string EffectId,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    InterlockResult Result);

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
    decimal Value);
