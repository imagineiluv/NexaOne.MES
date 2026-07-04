using FluentAssertions;
using NexaOne.Server.Realtime;
using NexaOne.Web.Services.Meta;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>P3-20 — 셸 알림 센터 피드: 이벤트 매핑(알림성만), 최근 목록 유계(50)·최신순, 구독/해지.</summary>
public sealed class RealtimeAlertFeedTests
{
    [Theory]
    [InlineData("EquipmentAlarmRaised", "알람")]
    [InlineData("FdcAlarmCleared", "알람")]
    [InlineData("InterlockTriggered", "인터락")]
    [InlineData("WorkOrderCompleted", "작업지시")]
    public void ToAlert_maps_notification_worthy_events(string eventType, string expectedCategory)
    {
        var alert = RealtimeAlertFeed.ToAlert(eventType, "EQ-1");
        alert.Should().NotBeNull();
        alert!.Category.Should().Be(expectedCategory);
        alert.Title.Should().Contain("EQ-1");
    }

    [Theory]
    [InlineData("EquipmentStateChanged")]   // 수집 워커발 고빈도 — 벨 제외(화면 갱신은 ScreenRefreshNotifier 소관)
    [InlineData("SomethingElse")]           // 대시보드성 기타 이벤트
    public void ToAlert_returns_null_for_noise_events(string eventType)
        => RealtimeAlertFeed.ToAlert(eventType, "X").Should().BeNull();

    [Fact]
    public async Task Publish_keeps_recent_bounded_to_fifty_newest_first()
    {
        var feed = new RealtimeAlertFeed();
        for (var i = 1; i <= 55; i++)
            await feed.PublishAsync(new RealtimeAlert(DateTime.UtcNow, "알람", $"A-{i}"));

        feed.Recent.Should().HaveCount(50, "최근 목록은 50건 유계");
        feed.Recent[0].Title.Should().Be("A-55", "최신순 정렬");
        feed.Recent[^1].Title.Should().Be("A-6", "가장 오래된 5건은 밀려난다");
    }

    [Fact]
    public async Task Subscribe_receives_alerts_and_dispose_stops_delivery()
    {
        var feed = new RealtimeAlertFeed();
        var received = new List<string>();
        var sub = feed.Subscribe(a => { received.Add(a.Title); return Task.CompletedTask; });

        await feed.PublishAsync(new RealtimeAlert(DateTime.UtcNow, "알람", "첫 번째"));
        received.Should().ContainSingle().Which.Should().Be("첫 번째");

        sub.Dispose();
        await feed.PublishAsync(new RealtimeAlert(DateTime.UtcNow, "알람", "두 번째"));
        received.Should().ContainSingle("해지 후에는 전달되지 않아야 한다");
    }

    [Fact]
    public async Task Failing_subscriber_does_not_break_other_subscribers()
    {
        var feed = new RealtimeAlertFeed();
        var received = 0;
        feed.Subscribe(_ => throw new InvalidOperationException("회로 폐기 경합 모사"));
        feed.Subscribe(_ => { received++; return Task.CompletedTask; });

        await feed.PublishAsync(new RealtimeAlert(DateTime.UtcNow, "알람", "격리 확인"));
        received.Should().Be(1, "한 구독자의 예외가 다른 구독자 전달을 끊지 않아야 한다");
    }
}
