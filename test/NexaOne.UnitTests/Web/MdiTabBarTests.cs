using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Layout;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// MdiTabBar(§20.7 다중 탭 더티 가드/저장 후 닫기/전체 닫기) 헤드리스 렌더 회귀 안전망.
/// MdiTabService·DirtyTracker는 실인스턴스로 주입하고, MenuCacheService·MenuPersonalizationService는
/// IApiClient를 Mock해 실인스턴스로 구성한다(둘 다 sealed 구상 — Moq 불가, 생성자 주입만 가능).
/// NavigationManager는 bUnit FakeNavigationManager(기본 등록)를 사용한다.
///
/// 진입 시 OnInitializedAsync가 현재 위치("/")를 탭으로 자동 등록한다(메뉴 캐시 미스 → "대시보드" 폴백).
/// 따라서 모든 테스트는 "/" 탭 1개를 기본 상태로 시작한다.
/// </summary>
public sealed class MdiTabBarTests
{
    // 닫기 다이얼로그의 더티 가드 진입을 막는 위험을 줄이기 위해, 메뉴 캐시는 항상 빈 목록을 돌려준다
    // → FindByProgramIdAsync는 null → menuId null → RecordRecentMenu는 no-op(부수 기록 호출 없음).
    private static (TestContext ctx, MdiTabService tabs, DirtyTracker dirty) Setup()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var api = new Mock<IApiClient>();
        api.Setup(a => a.GetMenuAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<MenuItemDto>());

        var tabs  = new MdiTabService();
        var dirty = new DirtyTracker();

        ctx.Services.AddSingleton(tabs);
        ctx.Services.AddSingleton(dirty);
        ctx.Services.AddSingleton(new MenuCacheService(api.Object));
        ctx.Services.AddSingleton(new MenuPersonalizationService(api.Object));

        return (ctx, tabs, dirty);
    }

    // 진입 자동 등록("/" 탭)이 완료될 때까지 기다린다.
    private static IRenderedComponent<MdiTabBar> RenderBar(TestContext ctx, MdiTabService tabs)
    {
        var cut = ctx.RenderComponent<MdiTabBar>();
        cut.WaitForAssertion(
            () => tabs.Tabs.Should().ContainSingle("진입 시 현재 위치('/')가 탭 1개로 자동 등록돼야 한다"),
            TimeSpan.FromSeconds(2));
        return cut;
    }

    // ConfirmCloseDialog / 전체 닫기 다이얼로그의 액션 버튼을 텍스트로 찾아 클릭한다.
    private static IElement ButtonWithText(IRenderedComponent<MdiTabBar> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == text);

    // ─────────────────────────────────────────────────────────────────────────
    // (a) 더티 활성 탭에서 × 닫기 → ConfirmCloseDialog가 보인다
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 더티_활성탭_닫기클릭하면_저장확인_다이얼로그가_표시된다()
    {
        var (ctx, tabs, dirty) = Setup();
        using var _ = ctx;
        var cut = RenderBar(ctx, tabs);

        // 활성 탭("/")을 더티로 만든다 → DirtyTracker.StateChanged → 활성 탭 IsDirty 반영.
        cut.InvokeAsync(() => dirty.MarkDirty());
        cut.WaitForAssertion(() => tabs.ActiveTab!.IsDirty.Should().BeTrue("활성 탭이 더티로 표시돼야 한다"));

        // 다이얼로그는 아직 닫혀 있다(본문 문구로 판정).
        cut.Markup.Should().NotContain("어떻게 하시겠습니까", "닫기 전에는 저장 확인 다이얼로그가 없어야 한다");

        // × 닫기 클릭 → 더티이므로 즉시 닫지 않고 확인 다이얼로그를 띄운다.
        cut.Find(".mdi-tab-close").Click();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("어떻게 하시겠습니까",
                "더티 활성 탭의 × 닫기는 ConfirmCloseDialog(Visible)를 표시해야 한다"));
        cut.FindAll(".confirm-close-dialog").Should().NotBeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // (b) 저장핸들러 미등록 탭에서 '저장 후 닫기' → _confirmError 문구 + 탭 유지
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 저장핸들러_미등록탭_저장후닫기하면_안내문구_표시되고_탭은_유지된다()
    {
        var (ctx, tabs, dirty) = Setup();
        using var _ = ctx;
        var cut = RenderBar(ctx, tabs);

        // 두 번째 탭을 열어 활성화한다(SaveHandler 미등록 상태). 1탭만 닫으면 홈 재등록이 일어나므로 2탭 구성.
        cut.InvokeAsync(() => tabs.Open("/mdm/plant", "", "공장"));
        cut.WaitForAssertion(() => tabs.Tabs.Should().HaveCount(2));
        var target = tabs.ActiveTab!;
        target.MenuId.Should().Be("/mdm/plant", "두 번째로 연 탭이 활성이어야 한다");
        target.SaveHandler.Should().BeNull("이 탭에는 저장 핸들러가 등록되지 않았다");

        // 활성 탭을 더티로 만들고 × 닫기 → 확인 다이얼로그.
        cut.InvokeAsync(() => dirty.MarkDirty());
        cut.WaitForAssertion(() => target.IsDirty.Should().BeTrue());
        cut.FindAll(".mdi-tab-close")[1].Click();   // 두 번째 탭의 × (활성 더티 탭)
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("어떻게 하시겠습니까"));

        // '저장 후 닫기' 클릭 → 핸들러 미등록이므로 저장 실패 처리 → 안내 문구 + 탭 유지.
        ButtonWithText(cut, "저장 후 닫기").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("저장 명령이 등록되지 않았습니다",
                "핸들러 미등록 탭의 저장 후 닫기는 안내 문구를 표시해야 한다");
        });
        tabs.Tabs.Should().Contain(target, "저장에 실패했으므로 탭은 닫히지 않고 유지돼야 한다");
        cut.Markup.Should().Contain("어떻게 하시겠습니까", "저장 실패 시 확인 다이얼로그는 유지돼야 한다");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // (c) SaveHandler가 false 반환 → 다이얼로그 유지 + 탭 미제거 + 실패 문구
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 저장핸들러가_false반환하면_다이얼로그_유지되고_탭은_제거되지_않는다()
    {
        var (ctx, tabs, dirty) = Setup();
        using var _ = ctx;
        var cut = RenderBar(ctx, tabs);

        cut.InvokeAsync(() => tabs.Open("/mdm/plant", "", "공장"));
        cut.WaitForAssertion(() => tabs.Tabs.Should().HaveCount(2));
        var target = tabs.ActiveTab!;

        // 저장 핸들러가 실패(false)를 반환하도록 등록.
        var saveCalled = 0;
        cut.InvokeAsync(() => tabs.RegisterSaveHandler(() => { saveCalled++; return Task.FromResult(false); }));
        target.SaveHandler.Should().NotBeNull();

        cut.InvokeAsync(() => dirty.MarkDirty());
        cut.WaitForAssertion(() => target.IsDirty.Should().BeTrue());
        cut.FindAll(".mdi-tab-close")[1].Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("어떻게 하시겠습니까"));

        ButtonWithText(cut, "저장 후 닫기").Click();

        cut.WaitForAssertion(() =>
        {
            saveCalled.Should().Be(1, "저장 후 닫기는 등록된 저장 핸들러를 호출해야 한다");
            cut.Markup.Should().Contain("저장에 실패했습니다", "핸들러 false 반환 시 실패 문구를 표시해야 한다");
        });
        tabs.Tabs.Should().Contain(target, "저장 실패 시 탭은 제거되면 안 된다");
        cut.Markup.Should().Contain("어떻게 하시겠습니까", "저장 실패 시 확인 다이얼로그는 유지돼야 한다");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // (d) '저장 안 함'(DiscardAndClose) → Dirty.ClearAll + 탭 제거
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 저장안함_선택하면_더티가_초기화되고_탭이_제거된다()
    {
        var (ctx, tabs, dirty) = Setup();
        using var _ = ctx;
        var cut = RenderBar(ctx, tabs);

        cut.InvokeAsync(() => tabs.Open("/mdm/plant", "", "공장"));
        cut.WaitForAssertion(() => tabs.Tabs.Should().HaveCount(2));
        var target = tabs.ActiveTab!;

        cut.InvokeAsync(() => dirty.MarkDirty());
        cut.WaitForAssertion(() =>
        {
            target.IsDirty.Should().BeTrue();
            dirty.IsDirty.Should().BeTrue("DirtyTracker도 더티 상태여야 한다");
        });
        cut.FindAll(".mdi-tab-close")[1].Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("어떻게 하시겠습니까"));

        // '저장하지 않고 닫기' → ClearDirty(활성→Dirty.ClearAll) + CloseTab(제거).
        ButtonWithText(cut, "저장하지 않고 닫기").Click();

        cut.WaitForAssertion(() =>
        {
            tabs.Tabs.Should().NotContain(target, "저장 안 함은 탭을 제거해야 한다");
            dirty.IsDirty.Should().BeFalse("활성 탭 닫기 시 페이지 측 DirtyTracker도 ClearAll로 초기화돼야 한다");
        });
        // 닫힌 뒤에는 확인 다이얼로그가 사라진다.
        cut.Markup.Should().NotContain("어떻게 하시겠습니까", "닫기 완료 후 확인 다이얼로그는 닫혀야 한다");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // (e) 전체 닫기 → 더티 탭 목록이 렌더된다
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 전체닫기_클릭하면_더티_탭_목록이_렌더된다()
    {
        var (ctx, tabs, dirty) = Setup();
        using var _ = ctx;
        var cut = RenderBar(ctx, tabs);

        // 두 번째 탭(공장)을 열고 활성·더티로 만든다 → DirtyTabs에 포함.
        cut.InvokeAsync(() => tabs.Open("/mdm/plant", "", "공장 관리"));
        cut.WaitForAssertion(() => tabs.Tabs.Should().HaveCount(2));
        cut.InvokeAsync(() => dirty.MarkDirty());
        cut.WaitForAssertion(() => tabs.DirtyTabs.Should().ContainSingle());

        // '전체 닫기' 버튼 클릭 → 더티 탭이 있으므로 전체 닫기 다이얼로그(목록 표시).
        ButtonWithText(cut, "전체 닫기").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("저장되지 않은 탭이 있습니다",
                "더티 탭이 있는 전체 닫기는 확인 다이얼로그를 표시해야 한다");
        });
        var listItems = cut.FindAll(".mdi-dirty-list li").Select(li => li.TextContent.Trim()).ToList();
        listItems.Should().Contain("공장 관리", "더티 탭의 제목이 전체 닫기 목록에 렌더돼야 한다");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // (f) 탭이 1개면 '전체 닫기' 버튼이 노출되지 않는다
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 탭이_1개면_전체닫기_버튼이_노출되지_않는다()
    {
        var (ctx, tabs, dirty) = Setup();
        using var _ = ctx;
        var cut = RenderBar(ctx, tabs);

        tabs.Tabs.Should().ContainSingle("진입 직후에는 '/' 탭 1개만 있어야 한다");
        cut.FindAll(".mdi-tab-closeall").Should().BeEmpty("탭이 1개면 '전체 닫기' 버튼은 노출되면 안 된다");

        // 탭을 하나 더 열면 '전체 닫기' 버튼이 나타난다(대조 검증).
        cut.InvokeAsync(() => tabs.Open("/mdm/plant", "", "공장"));
        cut.WaitForAssertion(() =>
            cut.FindAll(".mdi-tab-closeall").Should().NotBeEmpty(
                "탭이 2개 이상이면 '전체 닫기' 버튼이 노출돼야 한다"));
    }
}
