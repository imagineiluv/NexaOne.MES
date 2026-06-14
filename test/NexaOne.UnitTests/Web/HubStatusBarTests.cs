using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Shared;
using NexaOne.Web.Services;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// HubStatusBar 재연결/끊김 배너(§20.9) — IHubStatusSource 추출 후 가능해진 bUnit 검증.
/// Reconnecting 배너, Disconnected 빨간 배너+'다시 연결' 버튼, 수동 재연결 중 빨간배너 억제, 상태변경 재렌더.
/// </summary>
public sealed class HubStatusBarTests
{
    // 상태를 임의로 구동·재연결 호출을 관측할 수 있는 IHubStatusSource 테스트 더블.
    private sealed class FakeHubStatus : IHubStatusSource
    {
        public HubStatus Status { get; private set; } = HubStatus.Idle;
        public event Action<HubStatus>? OnStatusChanged;
        public int ReconnectCalls { get; private set; }
        public TaskCompletionSource<bool>? ReconnectGate;

        public void Set(HubStatus s) { Status = s; OnStatusChanged?.Invoke(s); }

        public Task<bool> ReconnectAsync(CancellationToken ct = default)
        {
            ReconnectCalls++;
            return ReconnectGate?.Task ?? Task.FromResult(true);
        }
    }

    private static IRenderedComponent<HubStatusBar> Render(TestContext ctx, FakeHubStatus hub)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;   // NexaButton의 JS 상호작용 허용
        ctx.Services.AddSingleton<IHubStatusSource>(hub);
        return ctx.RenderComponent<HubStatusBar>();
    }

    [Fact]
    public void Reconnecting_status_shows_reconnecting_banner()
    {
        using var ctx = new TestContext();
        var hub = new FakeHubStatus();
        hub.Set(HubStatus.Reconnecting);
        var cut = Render(ctx, hub);

        cut.FindAll(".hub-status-reconnecting").Should().NotBeEmpty("Reconnecting이면 재연결 배너를 표시해야 한다");
        cut.Markup.Should().Contain("재연결하는 중");
        cut.FindAll(".hub-status-disconnected").Should().BeEmpty("Reconnecting 중엔 빨간(끊김) 배너가 아니어야 한다");
    }

    [Fact]
    public void Disconnected_status_shows_red_banner_with_reconnect_button()
    {
        using var ctx = new TestContext();
        var hub = new FakeHubStatus();
        hub.Set(HubStatus.Disconnected);
        var cut = Render(ctx, hub);

        cut.FindAll(".hub-status-disconnected").Should().NotBeEmpty("Disconnected면 빨간 배너를 표시해야 한다");
        cut.Markup.Should().Contain("실시간 연결이 끊어졌습니다");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "다시 연결",
            "Disconnected 배너에는 수동 재연결 버튼이 있어야 한다");
    }

    [Theory]
    [InlineData(HubStatus.Connected)]
    [InlineData(HubStatus.Idle)]
    [InlineData(HubStatus.Connecting)]
    public void Healthy_or_idle_status_shows_no_banner(HubStatus status)
    {
        using var ctx = new TestContext();
        var hub = new FakeHubStatus();
        hub.Set(status);
        var cut = Render(ctx, hub);

        cut.FindAll(".hub-status-bar").Should().BeEmpty($"{status} 상태에서는 상태 배너를 표시하지 않아야 한다");
    }

    [Fact]
    public void Status_change_event_rerenders_banner()
    {
        using var ctx = new TestContext();
        var hub = new FakeHubStatus();   // Idle → 배너 없음
        var cut = Render(ctx, hub);
        cut.FindAll(".hub-status-bar").Should().BeEmpty();

        hub.Set(HubStatus.Disconnected);   // OnStatusChanged → InvokeAsync(StateHasChanged)

        cut.WaitForAssertion(() =>
            cut.FindAll(".hub-status-disconnected").Should().NotBeEmpty(
                "상태 변경 이벤트가 오면 배너가 재렌더돼야 한다"),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Manual_reconnect_calls_reconnect_and_suppresses_red_banner_while_pending()
    {
        using var ctx = new TestContext();
        var hub = new FakeHubStatus { ReconnectGate = new TaskCompletionSource<bool>() };
        hub.Set(HubStatus.Disconnected);
        var cut = Render(ctx, hub);

        // '다시 연결' 클릭 → ManualReconnectAsync가 _reconnecting=true 설정 후 ReconnectAsync 대기(게이트 보류).
        cut.FindAll("button").First(b => b.TextContent.Trim() == "다시 연결").Click();

        hub.ReconnectCalls.Should().Be(1, "수동 재연결 버튼은 ReconnectAsync를 호출해야 한다");
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("다시 연결하는 중",
                "재연결 진행 중에는 '다시 연결하는 중' 배너로 진행을 표시해야 한다");
            cut.FindAll(".hub-status-disconnected").Should().BeEmpty(
                "재연결 진행 중에는 빨간(끊김) 배너로 되돌리지 않아야 한다(연타·혼동 방지)");
        }, TimeSpan.FromSeconds(2));

        // 재연결 완료 → _reconnecting=false → Status가 여전히 Disconnected라 빨간 배너 복귀.
        hub.ReconnectGate!.SetResult(false);
        cut.WaitForAssertion(() =>
            cut.FindAll(".hub-status-disconnected").Should().NotBeEmpty(
                "재연결 실패로 끝나면 다시 빨간 배너로 복귀해야 한다"),
            TimeSpan.FromSeconds(2));
    }
}
