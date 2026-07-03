using System.Collections.Concurrent;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Realtime;

/// <summary>화면 실시간 재조회 알림 허브(호스트 구현) — 인메모리 이벤트 버스의 도메인 이벤트를 구독 화면
/// (Blazor Server 회로의 MetaScreen)으로 팬아웃한다. 항상 등록(modules OFF에서도 주입 가능)하되,
/// 버스가 없으면 NotifyAsync가 호출되지 않아 화면은 폴링만 수행한다. 구독자 콜백 실패는 다른 구독자에
/// 전파하지 않는다(회로 하나의 예외가 전체 팬아웃을 끊지 않게).</summary>
public sealed class ScreenRefreshNotifier : IScreenRefreshNotifier
{
    private readonly ConcurrentDictionary<Guid, Func<Task>> _subscribers = new();

    public IDisposable Subscribe(Func<Task> onChanged)
    {
        var key = Guid.NewGuid();
        _subscribers[key] = onChanged;
        return new Subscription(this, key);
    }

    /// <summary>도메인 이벤트 발행 시 호출(InMemoryBusSubscriberService) — 전 구독 화면에 재조회 신호.</summary>
    public async Task NotifyAsync()
    {
        foreach (var subscriber in _subscribers.Values)
        {
            try { await subscriber(); }
            catch { /* 회로 폐기 경합 등 — 개별 구독자 실패는 무시(다음 이벤트에서 자연 해소) */ }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ScreenRefreshNotifier _owner;
        private readonly Guid _key;
        public Subscription(ScreenRefreshNotifier owner, Guid key) { _owner = owner; _key = key; }
        public void Dispose() => _owner._subscribers.TryRemove(_key, out _);
    }
}
