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

    /// <summary>인터락 규칙이 발동했을 때 발생한다. 인터락 이력 기록·설비 정지·SignalR 알림 등
    /// 후속 처리는 호스트(구독자)가 담당한다 (§10.4.2).</summary>
    public event EventHandler<FdcInterlockTriggeredEventArgs>? InterlockTriggered;

    public FdcCollectorService(FdcDataService dataService, FdcInterlockService? interlockService = null)
    {
        _dataService = dataService;
        _interlockService = interlockService;
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

        var recorded = await _dataService.RecordDataAsync(
            collectId: Guid.NewGuid().ToString("N"),
            equipmentId: equipmentId,
            parameterId: evt.TagName,
            value: value,
            quality: MapQuality(evt.Quality),
            ct: ct);

        if (recorded.IsFailure) return;   // 미정의 파라미터·검증 실패 — 인터락 평가 생략

        if (_interlockService is not null)
        {
            var interlock = await _interlockService.EvaluateAsync(equipmentId, evt.TagName, value, ct);
            if (interlock.IsTriggered)
                InterlockTriggered?.Invoke(this,
                    new FdcInterlockTriggeredEventArgs(equipmentId, evt.TagName, value, interlock));
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
