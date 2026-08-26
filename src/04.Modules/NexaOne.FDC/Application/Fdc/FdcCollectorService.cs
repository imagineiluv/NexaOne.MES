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

    // 현재 발동 중인 (설비|파라미터) 집합 — 중복 발동 방지 + 정상 복귀 시에만 해제 처리
    private readonly ConcurrentDictionary<string, byte> _activeInterlocks = new();
    // 현재 발생 중인 알람의 최고 심각도(레벨) — 심각도 상승(Warning→Critical) 통지 판단용
    private readonly ConcurrentDictionary<string, string> _activeAlarms = new();
    // (설비|파라미터) 키별 평가-기록-해제 직렬화 — 동시 태그 이벤트의 발동↔해제 순서 역전 방지
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyGates = new();

    /// <summary>인터락 규칙이 발동했을 때 발생한다. 인터락 이력 기록·설비 정지·SignalR 알림 등
    /// 후속 처리는 호스트(구독자)가 담당한다 (§10.4.2).</summary>
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

        // 품질이 Good이 아니면(연결 끊김/Bad → ToDecimal이 0으로 뭉갬) 평가·해제하지 않는다.
        // 0으로 뭉개진 값이 활성 인터락/알람을 거짓 해제하거나 저값 규칙을 거짓 발동시키는 것을 방지.
        if (sample.Quality != FdcSampleQuality.Good) return;

        // 같은 (설비|태그) 이벤트의 동시 처리를 직렬화 — TryAdd 후 RecordTrigger(INSERT)와
        // 정상 복귀 시 ResolveActive(미해제 행 해제)의 순서 역전(발동↔해제 비대칭)을 막는다.
        var key = $"{equipmentId}|{sample.ParameterId}";
        var gate = _keyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_interlockService is not null)
                await EvaluateInterlockAsync(equipmentId, sample.ParameterId, sample.Value, key, ct);
            if (_alarmService is not null)
                await EvaluateAlarmAsync(equipmentId, sample.ParameterId, sample.Value, key, ct);
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
            // 신규 발동만 기록·통지 (이미 발동 중이면 중복 억제)
            if (_activeInterlocks.TryAdd(key, 0))
            {
                try
                {
                    await _interlockService.RecordTriggerAsync(equipmentId, tagName, value, interlock, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 기록(INSERT) 실패 시 활성 표시를 롤백한다. 롤백하지 않으면 활성 표시만 남아 이후 발동이
                    // 영구 억제되고(STOP 등 안전 동작 누락), 정상 복귀 시 기록 없는 인터락을 거짓 해제한다.
                    // 표시를 제거해 다음 태그 변화에서 재평가·재기록·재통지되도록 하고, 이번 통지는 생략한다.
                    _activeInterlocks.TryRemove(key, out _);
                    return;
                }
                InterlockTriggered?.Invoke(this,
                    new FdcInterlockTriggeredEventArgs(equipmentId, tagName, value, interlock));
            }
        }
        else if (_activeInterlocks.TryRemove(key, out _))
        {
            // 발동 중이던 항목이 정상 범위로 복귀 — 미해제 이력 자동 해제
            await _interlockService.ResolveActiveAsync(equipmentId, tagName, ct);
            InterlockResolved?.Invoke(this,
                new FdcInterlockResolvedEventArgs(equipmentId, tagName, value));
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
                    return;
                }
                _activeAlarms[key] = top.AlarmLevel;   // 기록 성공 후에만 활성 레벨 갱신
                AlarmRaised?.Invoke(this, new FdcAlarmRaisedEventArgs(equipmentId, tagName, value, top));
            }
        }
        else if (_activeAlarms.TryRemove(key, out _))
        {
            await _alarmService.ClearActiveAsync(equipmentId, tagName, ct);
            AlarmCleared?.Invoke(this, new FdcAlarmClearedEventArgs(equipmentId, tagName, value));
        }
    }

    /// <summary>알람 심각도 순위(Critical &gt; Warning &gt; 기타). 심각도 상승 통지 판단에 사용.</summary>
    private static int SeverityRank(string level) => level switch
    {
        "Critical" => 2,
        "Warning"  => 1,
        _          => 0,
    };
}

/// <summary>인터락 규칙 발동 이벤트 인자 (§10.4.2).</summary>
public sealed record FdcInterlockTriggeredEventArgs(
    string EquipmentId,
    string ParameterId,
    decimal Value,
    InterlockResult Result);

/// <summary>인터락 해제(정상 복귀) 이벤트 인자 (§10.4.2).</summary>
public sealed record FdcInterlockResolvedEventArgs(
    string EquipmentId,
    string ParameterId,
    decimal Value);

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
