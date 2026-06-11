using NexaOne.Web.Services;
using NexaOne.Web.Services.Realtime;

namespace NexaOne.UnitTests.Web;

/// <summary>설계서 §20.9 — 허브 연결 끊김 시 주기 조회 폴백.</summary>
public sealed class FallbackPollerTests
{
    private sealed class FakeHubStatus : IHubStatusSource
    {
        public HubStatus Status { get; private set; } = HubStatus.Idle;
        public event Action<HubStatus>? OnStatusChanged;

        public void Set(HubStatus status)
        {
            Status = status;
            OnStatusChanged?.Invoke(status);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue();
    }

    [Fact]
    public async Task Polls_periodically_while_disconnected()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(50));

        hub.Set(HubStatus.Disconnected);

        await WaitUntilAsync(() => Volatile.Read(ref count) >= 2);
        poller.IsPolling.Should().BeTrue();
    }

    [Fact]
    public async Task Stops_polling_when_reconnected()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(50));

        hub.Set(HubStatus.Disconnected);
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 1);

        hub.Set(HubStatus.Connected);
        poller.IsPolling.Should().BeFalse();

        await Task.Delay(100);   // 진행 중이던 틱이 정리될 시간
        var snapshot = Volatile.Read(ref count);
        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(snapshot);
    }

    [Fact]
    public async Task Starts_polling_if_already_disconnected_at_construction()
    {
        var hub = new FakeHubStatus();
        hub.Set(HubStatus.Disconnected);

        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(50));

        await WaitUntilAsync(() => Volatile.Read(ref count) >= 1);
    }

    [Theory]
    [InlineData(HubStatus.Idle)]
    [InlineData(HubStatus.Connecting)]
    [InlineData(HubStatus.Connected)]
    [InlineData(HubStatus.Reconnecting)]
    public async Task Does_not_poll_unless_disconnected(HubStatus status)
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(50));

        hub.Set(status);

        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(0);
        poller.IsPolling.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_stops_polling_and_ignores_later_status_changes()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(50));

        hub.Set(HubStatus.Disconnected);
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 1);

        poller.Dispose();
        await Task.Delay(100);
        var snapshot = Volatile.Read(ref count);

        hub.Set(HubStatus.Disconnected);   // dispose 이후 상태 변화는 무시돼야 한다
        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(snapshot);
    }

    [Fact]
    public async Task First_poll_fires_immediately_on_fallback_entry()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30));   // 주기를 길게 — 첫 조회가 주기 대기 없이 즉시 와야 한다 (§20.9)

        hub.Set(HubStatus.Disconnected);

        await WaitUntilAsync(() => Volatile.Read(ref count) >= 1, 1000);
    }

    [Fact]
    public async Task Reconnect_triggers_single_catch_up_refresh()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30));

        hub.Set(HubStatus.Disconnected);
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 1);
        var beforeReconnect = Volatile.Read(ref count);

        // §20.9 보상 조회: 재연결 성공 시 끊김 구간에 놓친 이벤트를 1회 따라잡는다
        hub.Set(HubStatus.Connected);

        await WaitUntilAsync(() => Volatile.Read(ref count) == beforeReconnect + 1);
        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(beforeReconnect + 1);   // 1회뿐, 이후 안정
        poller.IsPolling.Should().BeFalse();
    }

    [Fact]
    public async Task Reconnecting_to_connected_also_triggers_catch_up()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30));

        hub.Set(HubStatus.Reconnecting);   // 자동 재연결 구간 — 폴링은 하지 않는다
        await Task.Delay(100);
        Volatile.Read(ref count).Should().Be(0);

        hub.Set(HubStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref count) == 1);
        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(1);
    }

    [Fact]
    public async Task Initial_connection_does_not_trigger_catch_up()
    {
        var hub = new FakeHubStatus();
        var count = 0;
        using var poller = new FallbackPoller(hub,
            () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            TimeSpan.FromSeconds(30));

        // 페이지 진입 직후의 최초 연결 — 페이지가 어차피 초기 조회를 수행하므로 보상 조회 대상이 아니다
        hub.Set(HubStatus.Connecting);
        hub.Set(HubStatus.Connected);

        await Task.Delay(300);
        Volatile.Read(ref count).Should().Be(0);
    }

    [Fact]
    public async Task Slow_refresh_skips_overlapping_ticks()
    {
        var hub = new FakeHubStatus();
        var concurrent = 0;
        var maxConcurrent = 0;
        using var poller = new FallbackPoller(hub,
            async () =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                await Task.Delay(200);   // 폴링 주기보다 오래 걸리는 갱신
                Interlocked.Decrement(ref concurrent);
            },
            TimeSpan.FromMilliseconds(50));

        hub.Set(HubStatus.Disconnected);

        await Task.Delay(600);
        Volatile.Read(ref maxConcurrent).Should().Be(1);   // 중첩 조회가 없어야 한다
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
