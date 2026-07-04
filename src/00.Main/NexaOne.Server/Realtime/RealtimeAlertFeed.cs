using System.Collections.Concurrent;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Realtime;

/// <summary>셸 알림 센터 피드(호스트 구현, P3-20) — 이벤트 버스의 알림성 이벤트(알람/인터락/작업지시)를
/// 회로 셸(MesShellLayout 벨)로 팬아웃하고 최근 50건을 보관한다. ScreenRefreshNotifier와 동일 규약:
/// 항상 등록, 구독자 실패는 격리, 버스가 없으면(modules OFF) 조용히 비어 있다.</summary>
public sealed class RealtimeAlertFeed : IRealtimeAlertFeed
{
    private const int MaxRecent = 50;
    private readonly object _gate = new();
    private readonly LinkedList<RealtimeAlert> _recent = new();
    private readonly ConcurrentDictionary<Guid, Func<RealtimeAlert, Task>> _subscribers = new();

    public IReadOnlyList<RealtimeAlert> Recent
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    public IDisposable Subscribe(Func<RealtimeAlert, Task> callback)
    {
        var key = Guid.NewGuid();
        _subscribers[key] = callback;
        return new Subscription(this, key);
    }

    /// <summary>버스 구독자(InMemoryBusSubscriberService)가 알림성 이벤트를 밀어 넣는다.</summary>
    public async Task PublishAsync(RealtimeAlert alert)
    {
        lock (_gate)
        {
            _recent.AddFirst(alert);
            while (_recent.Count > MaxRecent) _recent.RemoveLast();
        }
        foreach (var subscriber in _subscribers.Values)
        {
            try { await subscriber(alert); }
            catch { /* 회로 폐기 경합 등 — 개별 구독자 실패 격리 */ }
        }
    }

    /// <summary>도메인 이벤트 → 알림 변환. 알림 가치가 없는 유형(대시보드성·고빈도 상태 변경)은 null.
    /// 이벤트 유형 추가 시 RealtimeNotificationDispatch와 함께 이 매핑도 검토한다.</summary>
    public static RealtimeAlert? ToAlert(string eventType, string aggregateId) => eventType switch
    {
        "EquipmentAlarmRaised" => new(DateTime.UtcNow, "알람", $"설비 알람 발생: {aggregateId}"),
        "EquipmentAlarmCleared" => new(DateTime.UtcNow, "알람", $"설비 알람 해제: {aggregateId}"),
        "FdcAlarmRaised" => new(DateTime.UtcNow, "알람", $"FDC 알람 발생: {aggregateId}"),
        "FdcAlarmCleared" => new(DateTime.UtcNow, "알람", $"FDC 알람 해제: {aggregateId}"),
        "InterlockTriggered" => new(DateTime.UtcNow, "인터락", $"인터락 발동: {aggregateId}"),
        "WorkOrderStarted" => new(DateTime.UtcNow, "작업지시", $"작업지시 시작: {aggregateId}"),
        "WorkOrderCompleted" => new(DateTime.UtcNow, "작업지시", $"작업지시 완료: {aggregateId}"),
        "WorkOrderCancelled" => new(DateTime.UtcNow, "작업지시", $"작업지시 취소: {aggregateId}"),
        // EquipmentStateChanged는 수집 워커발 고빈도라 벨 알림에서 제외(화면 갱신은 ScreenRefreshNotifier가 담당).
        _ => null,
    };

    private sealed class Subscription : IDisposable
    {
        private readonly RealtimeAlertFeed _owner;
        private readonly Guid _key;
        public Subscription(RealtimeAlertFeed owner, Guid key) { _owner = owner; _key = key; }
        public void Dispose() => _owner._subscribers.TryRemove(_key, out _);
    }
}
