using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Shared;
using NexaOne.Web.Services;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// ApiToastHost(전역 API 오류 토스트)의 헤드리스 렌더 검증 — 통지 표시, 중복 억제, 수동 닫기.
/// ApiClient가 403/5xx를 ApiNotificationService로 발신하면 화면 상단에 노출되는지 보장한다.
/// </summary>
public sealed class ApiToastHostTests
{
    private static (TestContext ctx, ApiNotificationService notifier) Setup()
    {
        var ctx = new TestContext();
        ctx.Services.AddSingleton<ApiNotificationService>();
        return (ctx, ctx.Services.GetRequiredService<ApiNotificationService>());
    }

    [Fact]
    public void Notify_renders_toast_with_message()
    {
        var (ctx, notifier) = Setup();
        using var _ = ctx;
        var cut = ctx.RenderComponent<ApiToastHost>();

        cut.FindAll("[role='alert']").Should().BeEmpty("초기에는 토스트가 없다");

        notifier.Notify("이 작업을 수행할 권한이 없습니다.");

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("이 작업을 수행할 권한이 없습니다."),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Duplicate_message_is_suppressed_but_distinct_is_shown()
    {
        var (ctx, notifier) = Setup();
        using var _ = ctx;
        var cut = ctx.RenderComponent<ApiToastHost>();

        notifier.Notify("AAA");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("AAA"));
        notifier.Notify("AAA");   // 중복 — 억제되어야 한다
        notifier.Notify("BBB");   // 구별되는 메시지

        // BBB가 렌더되면 그 이전에 큐잉된 AAA 통지 2건은 모두 처리된 시점이다.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("BBB"), TimeSpan.FromSeconds(2));
        cut.FindAll("[role='alert']").Should().HaveCount(2, "중복 AAA는 억제되고 AAA·BBB 둘만 남는다");
    }

    [Fact]
    public void Dismiss_button_removes_toast()
    {
        var (ctx, notifier) = Setup();
        using var _ = ctx;
        var cut = ctx.RenderComponent<ApiToastHost>();

        notifier.Notify("닫을 메시지");
        cut.WaitForAssertion(() => cut.FindAll("[role='alert']").Should().HaveCount(1));

        cut.Find("button[aria-label='닫기']").Click();

        cut.WaitForAssertion(
            () => cut.FindAll("[role='alert']").Should().BeEmpty(),
            TimeSpan.FromSeconds(2));
    }
}
