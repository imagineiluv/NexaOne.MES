namespace NexaOne.Web.Services.Realtime;

/// <summary>
/// 실시간 이벤트 폭주를 윈도(기본 1초) 단위로 병합하는 트리거 (설계서 §20.9).
/// <list type="bullet">
/// <item><c>leadingEdge=true</c>: 첫 신호는 즉시 반영하고, 윈도 내 추가 신호는 마지막 1회만 트레일링 반영한다
/// — 설비 상태 '즉시 반영 + 1초 내 마지막 값', 알람 '즉시 Push + 1초 병합'.</item>
/// <item><c>leadingEdge=false</c>: 신호를 모아 윈도 경계에서만 반영한다 — FDC 수집값 배치 렌더링.</item>
/// </list>
/// 콜백은 타이머 스레드에서 호출될 수 있으므로 Blazor 컴포넌트에서는 InvokeAsync로 감싼다 (§20.9).
/// </summary>
public sealed class CoalescingTrigger : IDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    private readonly Func<Task> _flush;
    private readonly TimeSpan _window;
    private readonly bool _leadingEdge;
    private readonly Timer _timer;
    private readonly object _sync = new();

    private bool _windowOpen;
    private bool _pending;
    private bool _flushing;
    private bool _flushQueued;
    private bool _disposed;

    public CoalescingTrigger(Func<Task> flush, TimeSpan? window = null, bool leadingEdge = true)
    {
        _flush = flush;
        _window = window ?? DefaultWindow;
        _leadingEdge = leadingEdge;
        _timer = new Timer(_ => OnWindowElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Signal()
    {
        var fireNow = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (_windowOpen)
            {
                _pending = true;   // 윈도 내 추가 신호 — 마지막 1회만 트레일링 반영
                return;
            }

            _windowOpen = true;
            if (_leadingEdge) fireNow = true;
            else _pending = true;
            _timer.Change(_window, Timeout.InfiniteTimeSpan);
        }

        if (fireNow) RequestFlush();
    }

    private void OnWindowElapsed()
    {
        var fire = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (_pending)
            {
                _pending = false;
                fire = true;
                _timer.Change(_window, Timeout.InfiniteTimeSpan);   // 트레일링 반영 후 윈도 연장
            }
            else
            {
                _windowOpen = false;
            }
        }

        if (fire) RequestFlush();
    }

    // 플러시는 동시에 1개만 실행한다. 진행 중에 발화 시점이 오면 즉시 띄우지 않고 합류시켰다가
    // 현재 플러시 완료 후 1회만 이어서 실행 — 응답 완료 순서 역전으로 오래된 스냅숏이
    // 마지막에 반영되는 것을 막아 '마지막 값이 최종 표시'를 보장한다 (§20.9).
    private void RequestFlush()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_flushing)
            {
                _flushQueued = true;
                return;
            }
            _flushing = true;
        }

        _ = RunFlushAsync();
    }

    private async Task RunFlushAsync()
    {
        while (true)
        {
            try { await _flush(); }
            catch { /* 갱신 실패는 다음 신호/폴백에서 복구 — 타이머 스레드로 예외 전파 금지 */ }

            lock (_sync)
            {
                if (_disposed || !_flushQueued)
                {
                    _flushing = false;
                    return;
                }
                _flushQueued = false;   // 진행 중 합류한 발화 1회를 이어서 실행 (최신 데이터 재조회)
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
