using System.Collections.Concurrent;
using NexaOne.Common.Telemetry;
using NexaOne.FDC.Infrastructure.Equipment;
using NexusLogic.Plc.Abstractions.Models;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>
/// OPC-UA 설비 구독을 FDC 수집 데이터 적재로 잇는 오케스트레이터 (§10.4).
/// <see cref="OpcUaDeviceInterface"/>(NexusLogic <c>IPlcConnection</c>)의 태그 변경 이벤트를 받아
/// <see cref="FdcDataService"/>로 FDC_TB_COLLECT_DATA에 기록한다.
/// </summary>
/// <remarks>
/// 설비 등록·시작(PlantController/Machine lifecycle)은 호스트 측이 담당하고, 본 서비스는
/// 이미 Start된 디바이스의 연결에 구독을 걸어 데이터 흐름만 책임진다. 파라미터 미정의·검증 실패는
/// 수집 루프를 막지 않도록 예외를 전파하지 않는다(설비 데이터 폭주 시 한 건 실패가 전체를 멈추지 않게).
/// </remarks>
public sealed class FdcCollectorService
{
    private readonly FdcDataService _dataService;
    private readonly FdcInterlockService? _interlockService;
    private readonly FdcAlarmService? _alarmService;

    // 현재 발동 중인 (설비|파라미터) 집합 — 중복 발동 방지 + 정상 복귀 시에만 해제 처리
    private readonly ConcurrentDictionary<string, byte> _activeInterlocks = new();
    private readonly ConcurrentDictionary<string, byte> _activeAlarms = new();

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

    /// <summary>이미 초기화/시작된 디바이스의 연결에 태그 구독을 걸고, 변경 이벤트를 수집 데이터로 적재한다.</summary>
    public async Task StartCollectingAsync(
        OpcUaDeviceInterface device,
        IEnumerable<PlcSubscription> subscriptions,
        CancellationToken ct = default)
    {
        var conn = device.Connection
            ?? throw new InvalidOperationException(
                $"Device '{device.InterfaceName}' is not initialized (connect first via Machine lifecycle).");

        await conn.SubscriptionProvider.StartAsync(
            conn.Endpoint,
            subscriptions,
            evt => OnTagChangeAsync(device.InterfaceName, evt, ct),
            ct);
    }

    /// <summary>태그 변경 1건을 수집 데이터로 적재하고, 인터락 규칙을 평가한다.
    /// 파라미터 미정의·검증 실패는 삼킨다(폭주 방지) — 이 경우 인터락 평가도 건너뛴다.</summary>
    public async Task OnTagChangeAsync(string equipmentId, PlcTagChangeEvent evt, CancellationToken ct = default)
    {
        var value = ToDecimal(evt.After);
        var quality = MapQuality(evt.Quality);

        var recorded = await _dataService.RecordDataAsync(
            collectId: Guid.NewGuid().ToString("N"),
            equipmentId: equipmentId,
            parameterId: evt.TagName,
            value: value,
            quality: quality,
            ct: ct);

        if (recorded.IsFailure) return;   // 미정의 파라미터·검증 실패 — 인터락 평가 생략

        // §17.5 nexames_fdc_collection_rate 적응 — 적재 성공 1건 계측 (대시보드가 기대 대비 수집률 산정)
        NexaMesMetrics.FdcCollected.Add(1,
            new KeyValuePair<string, object?>("equipmentId", equipmentId),
            new KeyValuePair<string, object?>("quality", quality));

        if (_interlockService is not null)
        {
            var key = $"{equipmentId}|{evt.TagName}";
            var interlock = await _interlockService.EvaluateAsync(equipmentId, evt.TagName, value, ct);

            if (interlock.IsTriggered)
            {
                // 신규 발동만 기록·통지 (이미 발동 중이면 중복 억제)
                if (_activeInterlocks.TryAdd(key, 0))
                {
                    await _interlockService.RecordTriggerAsync(equipmentId, evt.TagName, value, interlock, ct);
                    InterlockTriggered?.Invoke(this,
                        new FdcInterlockTriggeredEventArgs(equipmentId, evt.TagName, value, interlock));
                }
            }
            else if (_activeInterlocks.TryRemove(key, out _))
            {
                // 발동 중이던 항목이 정상 범위로 복귀 — 미해제 이력 자동 해제
                await _interlockService.ResolveActiveAsync(equipmentId, evt.TagName, ct);
                InterlockResolved?.Invoke(this,
                    new FdcInterlockResolvedEventArgs(equipmentId, evt.TagName, value));
            }
        }

        if (_alarmService is not null)
        {
            var key = $"{equipmentId}|{evt.TagName}";
            var alarms = await _alarmService.EvaluateAsync(equipmentId, evt.TagName, value, ct);

            if (alarms.Count > 0)
            {
                // 신규 발생만 기록·통지 (발생 중이면 중복 억제). 동시에 여러 레벨이 잡히면 가장 심각한 것을 통지
                if (_activeAlarms.TryAdd(key, 0))
                {
                    var top = alarms.Any(a => a.AlarmLevel == "Critical")
                        ? alarms.First(a => a.AlarmLevel == "Critical")
                        : alarms[0];
                    await _alarmService.RecordAsync(equipmentId, evt.TagName, value, top, ct);
                    AlarmRaised?.Invoke(this, new FdcAlarmRaisedEventArgs(equipmentId, evt.TagName, value, top));
                }
            }
            else if (_activeAlarms.TryRemove(key, out _))
            {
                await _alarmService.ClearActiveAsync(equipmentId, evt.TagName, ct);
                AlarmCleared?.Invoke(this, new FdcAlarmClearedEventArgs(equipmentId, evt.TagName, value));
            }
        }
    }

    /// <summary>NexusLogic <see cref="PlcQuality"/> → FDC 수집 품질("Good"/"Bad"/"Uncertain") 매핑.</summary>
    public static string MapQuality(PlcQuality quality) => quality switch
    {
        PlcQuality.Good      => "Good",
        PlcQuality.Uncertain => "Uncertain",
        _                    => "Bad",   // Bad / Timeout / Disconnected / NotSupported
    };

    /// <summary>태그 값(object?) → decimal 변환. null·변환 불가 값은 0으로 처리한다.</summary>
    public static decimal ToDecimal(object? value)
    {
        if (value is null) return 0m;
        try
        {
            return Convert.ToDecimal(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0m;
        }
    }
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
