using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace NexaOne.Common.Telemetry;

/// <summary>
/// §17.5 <c>nexames_equipment_channel_status</c> — 설비 통신 채널의 연결 상태(정상 1/비정상 0)를 보관한다.
/// FDC 수집 호스트가 채널 수립/해제 시 <see cref="Set"/>로 갱신하고, OpenTelemetry ObservableGauge가
/// <see cref="Observe"/>로 현재 상태를 수집한다.
/// </summary>
/// <remarks>
/// 현재는 수집 호스트의 채널 수립(1)·해제(0)를 반영한다. 운영 중 일시적 단절/재연결의 실시간 반영은
/// NexaLogic 연결 상태 변경 이벤트 연동 시점의 과제로 둔다(§17.3 동일 관례).
/// </remarks>
public sealed class EquipmentChannelStatusRegistry
{
    // (equipmentId, channelType, plantId) → 상태(1/0)
    private readonly ConcurrentDictionary<(string Equipment, string Channel, string Plant), int> _status = new();

    /// <summary>채널 연결 상태를 설정한다(정상 true → 1, 비정상 false → 0). plantId에 null을 넘기면 GLOBAL로 취급.</summary>
    public void Set(string equipmentId, string channelType, bool up, string? plantId = "GLOBAL")
        => _status[(equipmentId, channelType, plantId ?? "GLOBAL")] = up ? 1 : 0;

    /// <summary>채널 항목을 제거한다(더 이상 관측하지 않음). plantId에 null을 넘기면 GLOBAL로 취급.</summary>
    public void Remove(string equipmentId, string channelType, string? plantId = "GLOBAL")
        => _status.TryRemove((equipmentId, channelType, plantId ?? "GLOBAL"), out _);

    /// <summary>현재 등록된 모든 채널의 상태 스냅샷.</summary>
    public IEnumerable<Measurement<int>> Observe()
    {
        var result = new List<Measurement<int>>(_status.Count);
        foreach (var entry in _status)
            result.Add(new Measurement<int>(entry.Value,
                new KeyValuePair<string, object?>("equipmentId", entry.Key.Equipment),
                new KeyValuePair<string, object?>("channelType", entry.Key.Channel),
                new KeyValuePair<string, object?>("plantId", entry.Key.Plant)));
        return result;
    }
}
