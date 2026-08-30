using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NexaOne.Server.Components;
using NexaOne.Web.Services.Api;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>작업 채널의 화면 우선순위·안전한 진입 링크·PDA 터치 규격을 고정하는 UI 회귀 가드.</summary>
public sealed class OperatorChannelUxContractTests : BunitContext
{
    [Fact]
    public void Catalog_renders_channel_context_and_only_safe_entry_paths_as_actions()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetScreenDefinitionsAsync("MOBILE", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScreenDefinitionRecordDto>
            {
                new("MOB_SCAN", "Lot 스캔", "{}", "MOBILE", "/Mobile/MOB_SCAN"),
                new("MOB_WRONG", "잘못된 경로", "{}", "MOBILE", "/POP/MOB_WRONG"),
            });
        Services.AddSingleton(api.Object);

        var cut = Render<OperatorChannelCatalog>(parameters => parameters
            .Add(x => x.TargetChannel, "MOBILE")
            .Add(x => x.BasePath, "/Mobile")
            .Add(x => x.Title, "모바일 작업")
            .Add(x => x.Description, "배정된 작업을 선택하세요."));

        cut.WaitForAssertion(() =>
        {
            cut.Find("#operator-work-catalog").ClassList.Should().Contain("is-mobile");
            cut.Find(".operator-count strong").TextContent.Trim().Should().Be("2");
            cut.FindAll("a.operator-screen-card").Should().ContainSingle();
            cut.Find("a.operator-screen-card").GetAttribute("href").Should().Be("/Mobile/MOB_SCAN");
            cut.Find("a.operator-screen-card").GetAttribute("aria-label").Should().Be("Lot 스캔 작업 시작");
            cut.Find("article.operator-screen-card.invalid").TextContent.Should().Contain("잘못된 진입 경로");
        });
    }

    [Fact]
    public void Catalog_preserves_encoded_device_identity_in_safe_screen_links()
    {
        const string deviceId = "PDA 01/검사&상태=1";
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetScreenDefinitionsAsync("MOBILE", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScreenDefinitionRecordDto>
            {
                new("MOB_SCAN", "Lot 스캔", "{}", "MOBILE", "/Mobile/MOB_SCAN"),
            });
        Services.AddSingleton(api.Object);

        var cut = Render<OperatorChannelCatalog>(parameters => parameters
            .Add(x => x.TargetChannel, "MOBILE")
            .Add(x => x.BasePath, "/Mobile")
            .Add(x => x.Title, "모바일 작업")
            .Add(x => x.Description, "배정된 작업을 선택하세요.")
            .Add(x => x.DeviceId, deviceId));

        cut.WaitForAssertion(() =>
            cut.Find("a.operator-screen-card").GetAttribute("href").Should().Be(
                $"/Mobile/MOB_SCAN?deviceId={Uri.EscapeDataString(deviceId)}"));
    }

    [Fact]
    public void Channel_home_and_catalog_navigation_keep_device_query_contract()
    {
        foreach (var home in new[] { "HostMobileHome.razor", "HostPopHome.razor" })
        {
            var source = File.ReadAllText(RepoFile(
                "src", "00.Main", "NexaOne.Server", "Components", "Pages", home));
            source.Should().Contain("SupplyParameterFromQuery(Name = \"deviceId\")");
            source.Should().Contain("DeviceId=\"@DeviceId\"");
        }

        var screen = File.ReadAllText(RepoFile(
            "src", "00.Main", "NexaOne.Server", "Components", "OperatorChannelScreen.razor"));
        screen.Should().Contain("href=\"@CatalogPath\"");
        screen.Should().NotContain("href=\"@BasePath\"");
        screen.Should().Contain("Uri.EscapeDataString(DeviceId.Trim())");
    }

    [Fact]
    public void Mobile_home_keeps_work_actions_first_and_css_uses_shared_touch_targets()
    {
        var mobileHome = File.ReadAllText(RepoFile(
            "src", "00.Main", "NexaOne.Server", "Components", "Pages", "HostMobileHome.razor"));
        mobileHome.IndexOf("<OperatorChannelCatalog", StringComparison.Ordinal).Should().BeLessThan(
            mobileHome.IndexOf("<OperatorQualitySnapshot", StringComparison.Ordinal),
            "작업자는 PDA 첫 화면에서 지표보다 작업 시작 액션을 먼저 만나야 한다");

        var css = File.ReadAllText(RepoFile(
            "src", "00.Main", "NexaOne.Server", "wwwroot", "css", "nexaone.css"));
        var tokens = File.ReadAllText(RepoFile("tokens.css"));
        tokens.Should().Contain("--touch-target: 2.75rem");
        tokens.Should().Contain("--kiosk-target: 3.5rem");
        tokens.Should().Contain("--nx-touch-min: var(--touch-target)");
        tokens.Should().Contain("--nx-kiosk-min: var(--kiosk-target)");
        css.Should().Contain(".operator-mobile button, .operator-mobile .rz-button, .operator-mobile input, .operator-mobile select { min-height: var(--nx-touch-min); }");
        css.Should().Contain(".operator-bottom-nav a { min-height: var(--nx-touch-min)");
        css.Should().Contain("grid-template-columns: repeat(3, 1fr)");
        css.Should().Contain("@media (max-width: 420px)", "390px PDA 폭 전용 압축 규칙이 있어야 한다");
        css.Should().Contain("@media (prefers-reduced-motion: reduce)");
        css.Should().Contain(".operator-runtime-frame { width: 100%; max-width: 100%; min-width: 0; overflow-x: clip; }");
        css.Should().Contain(".operator-pop .layout-section:has(.meta-field) .layout-command-wrap",
            "POP 명령과 비활성 사유는 하나의 동일 폭 액션 단위로 정렬되어야 한다");
        css.Should().Contain(".operator-mobile .layout-command-wrap { width: 100%; align-items: stretch; }",
            "PDA 명령은 좁은 화면에서 전체 폭 터치 대상으로 표시되어야 한다");

        var screen = File.ReadAllText(RepoFile(
            "src", "00.Main", "NexaOne.Server", "Components", "OperatorChannelScreen.razor"));
        screen.Should().Contain("operator-workflow-nav");
        screen.Should().Contain("작업지시 선택");
        screen.Should().Contain("상세 · 실행");
        screen.Should().Contain("실행 이력");

        var script = File.ReadAllText(RepoFile(
            "src", "00.Main", "NexaOne.Server", "wwwroot", "js", "nexaone-operator.js"));
        script.Should().Contain("scrollIntoView");
        script.Should().Contain("prefers-reduced-motion: reduce");
    }

    [Fact]
    public void Mrp_conversion_dialog_has_no_static_inline_styles()
    {
        var source = File.ReadAllText(RepoFile(
            "src", "00.Main", "NexaOne.Server", "Components", "MrpConversionDialog.razor"));

        source.Should().NotContain("style=", "정적 다이얼로그 표면은 --nx-* 토큰 기반 클래스가 소유해야 한다");
        source.Should().NotContain(" Style=\"", "Radzen 폭도 공통 CSS 클래스가 소유해야 다크·반응형 규칙을 공유한다");
        source.Should().Contain("mrp-conversion-table-wrap");
        source.Should().Contain("mrp-conversion-actions");
    }

    private static string RepoFile(params string[] segments)
        => RepositorySource.GetFile(segments);
}
