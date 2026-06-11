namespace NexaOne.Web.Services.Realtime;

/// <summary>
/// SignalR 연결이 끊긴 동안(<see cref="HubStatus.Disconnected"/>) 주기 조회로 데이터를 갱신하는
/// 폴백 (설계서 §20.9). 폴백 진입 시 즉시 1회 조회하고, Connected 복귀 시 자동으로 멈추되
/// 끊김 구간(Reconnecting/Disconnected)에 발생한 변경분을 1회 보상 조회(catch-up)한다.
/// 콜백은 타이머 스레드에서 호출될 수 있으므로 Blazor 컴포넌트에서는 InvokeAsync로 감싼다.
/// </summary>
public sealed class FallbackPoller : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly IHubStatusSource _hub;
    private readonly Func<Task> _refresh;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly object _sync = new();

    private HubStatus _lastStatus;
    private bool _polling;
    private bool _refreshing;
    private bool _disposed;

    public FallbackPoller(IHubStatusSource hub, Func<Task> refresh, TimeSpan? interval = null)
    {
        _hub = hub;
        _refresh = refresh;
        _interval = interval ?? DefaultInterval;
        _timer = new Timer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // 생성 시점 상태를 기준선으로 삼는다 — 페이지 진입 직후의 Connected를 '재연결 성공'으로
        // 오인해 불필요한 보상 조회를 하지 않기 위함 (페이지는 어차피 초기 조회를 수행한다)
        _lastStatus = _hub.Status;
        _hub.OnStatusChanged += OnStatusChanged;
        OnStatusChanged(_hub.Status);   // 생성 시점에 이미 끊겨 있으면 즉시 폴링 시작
    }

    public bool IsPolling
    {
        get { lock (_sync) return _polling; }
    }

    private void OnStatusChanged(HubStatus status)
    {
        var catchUp = false;
        lock (_sync)
        {
            if (_disposed) return;

            var prev = _lastStatus;
            _lastStatus = status;

            var shouldPoll = status == HubStatus.Disconnected;
            if (shouldPoll != _polling)
            {
                _polling = shouldPoll;
                // 폴백 진입 시 첫 주기를 기다리지 않고 즉시 1회 조회한다 (§20.9)
                if (shouldPoll) _timer.Change(TimeSpan.Zero, _interval);
                else _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            // §20.9 보상 조회: 재연결 성공 시 끊김 구간(Reconnecting/Disconnected)에 놓친
            // 이벤트를 1회 재조회로 따라잡는다 — 다음 이벤트까지 stale 화면이 유지되는 것을 방지
            if (status == HubStatus.Connected &&
                prev is HubStatus.Reconnecting or HubStatus.Disconnected &&
                !_refreshing)
            {
                _refreshing = true;
                catchUp = true;
            }
        }

        if (catchUp) _ = RunRefreshAsync();
    }

    private void OnTick()
    {
        lock (_sync)
        {
            // 이전 갱신이 끝나지 않았으면 이번 주기는 건너뛴다 (중첩 조회 방지)
            if (_disposed || !_polling || _refreshing) return;
            _refreshing = true;
        }

        _ = RunRefreshAsync();
    }

    private async Task RunRefreshAsync()
    {
        try { await _refresh(); }
        catch { /* 폴백 조회 실패는 다음 주기에 재시도 */ }
        finally { lock (_sync) _refreshing = false; }
    }

    public void Dispose()
    {
        _hub.OnStatusChanged -= OnStatusChanged;
        lock (_sync)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
