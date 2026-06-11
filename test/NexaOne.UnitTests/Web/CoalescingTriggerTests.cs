using NexaOne.Web.Services.Realtime;

namespace NexaOne.UnitTests.Web;

/// <summary>설계서 §20.9 — 실시간 이벤트 1초 병합/배치 트리거.</summary>
public sealed class CoalescingTriggerTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue();
    }

    [Fact]
    public async Task Leading_edge_fires_immediately_on_first_signal()
    {
        var count = 0;
        using var trigger = new CoalescingTrigger(
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(200));

        trigger.Signal();

        await WaitUntilAsync(() => Volatile.Read(ref count) == 1, 500);
        await Task.Delay(500);   // 추가 신호가 없으므로 트레일링은 발생하지 않는다
        Volatile.Read(ref count).Should().Be(1);
    }

    [Fact]
    public async Task Burst_coalesces_to_leading_plus_single_trailing()
    {
        var count = 0;
        using var trigger = new CoalescingTrigger(
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(150));

        for (var i = 0; i < 5; i++)
            trigger.Signal();   // 윈도 내 폭주 — 리딩 1회 + 트레일링 1회만 반영돼야 한다

        await WaitUntilAsync(() => Volatile.Read(ref count) == 2);
        await Task.Delay(500);
        Volatile.Read(ref count).Should().Be(2);
    }

    [Fact]
    public async Task Trailing_only_mode_batches_signals_to_one_flush()
    {
        var count = 0;
        using var trigger = new CoalescingTrigger(
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(500), leadingEdge: false);   // CI 스톨에도 즉시 단정이 안전하도록 넉넉한 윈도

        trigger.Signal();
        trigger.Signal();
        trigger.Signal();

        Volatile.Read(ref count).Should().Be(0);   // 배치 모드 — 윈도 경계 전에는 반영하지 않는다
        await WaitUntilAsync(() => Volatile.Read(ref count) == 1);
        await Task.Delay(700);
        Volatile.Read(ref count).Should().Be(1);
    }

    [Fact]
    public async Task New_signal_after_idle_window_fires_leading_again()
    {
        var count = 0;
        using var trigger = new CoalescingTrigger(
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(100));

        trigger.Signal();
        await WaitUntilAsync(() => Volatile.Read(ref count) == 1, 500);
        await Task.Delay(400);   // 윈도가 닫히길 기다린다

        trigger.Signal();
        await WaitUntilAsync(() => Volatile.Read(ref count) == 2, 500);
    }

    [Fact]
    public async Task Dispose_stops_pending_trailing_flush()
    {
        var count = 0;
        var trigger = new CoalescingTrigger(
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(150));

        trigger.Signal();   // 리딩 1회
        trigger.Signal();   // 트레일링 예약
        trigger.Dispose();

        await Task.Delay(500);
        Volatile.Read(ref count).Should().Be(1);   // 트레일링은 발생하지 않는다

        trigger.Signal();   // dispose 이후 신호는 무시
        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(1);
    }

    [Fact]
    public async Task Flush_exception_does_not_stop_subsequent_flushes()
    {
        var count = 0;
        using var trigger = new CoalescingTrigger(
            () =>
            {
                Interlocked.Increment(ref count);
                throw new InvalidOperationException("flush failure");
            },
            TimeSpan.FromMilliseconds(100));

        trigger.Signal();
        await WaitUntilAsync(() => Volatile.Read(ref count) == 1, 500);
        await Task.Delay(400);

        trigger.Signal();   // 이전 flush 예외에도 다음 신호는 정상 동작해야 한다
        await WaitUntilAsync(() => Volatile.Read(ref count) == 2, 500);
    }

    [Fact]
    public async Task Slow_flush_never_overlaps_even_under_continuous_signals()
    {
        // §20.9: flush(전체 재조회)가 윈도보다 오래 걸려도 동시 실행은 1개여야 한다 —
        // 응답 완료 순서 역전으로 오래된 스냅숏이 마지막에 표시되는 것을 막는다 (last-data-wins).
        var concurrent = 0;
        var maxConcurrent = 0;
        var count = 0;
        using var trigger = new CoalescingTrigger(
            async () =>
            {
                InterlockedMax(ref maxConcurrent, Interlocked.Increment(ref concurrent));
                Interlocked.Increment(ref count);
                await Task.Delay(200);   // 윈도(50ms)보다 오래 걸리는 flush
                Interlocked.Decrement(ref concurrent);
            },
            TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 6; i++)
        {
            trigger.Signal();
            await Task.Delay(60);   // 매 신호가 진행 중 flush에 합류하거나 새 윈도를 열도록 분산
        }

        // 신호가 멈춘 뒤 합류분까지 모두 소진될 때까지 기다린다
        await WaitUntilAsync(() => Volatile.Read(ref concurrent) == 0 && Volatile.Read(ref count) >= 2);
        await Task.Delay(500);

        Volatile.Read(ref maxConcurrent).Should().Be(1);   // 중첩 flush가 없어야 한다
        Volatile.Read(ref concurrent).Should().Be(0);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
