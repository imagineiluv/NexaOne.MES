using Bunit;
using Microsoft.AspNetCore.Components.Web;
using NexaOne.Web.Components.NexaUI;
using NexaOne.Web.Components.Shared;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 공통 모달(NexaModal·ConfirmCloseDialog) 접근성 회귀 안전망 — 49개 화면 중 31개가 NexaModal을 쓰므로
/// 이 한 컴포넌트의 접근성 시맨틱이 전 화면에 전파된다. WCAG 2.1.2(키보드 ESC)·4.1.2(role/aria 이름)를 검증한다.
/// FocusAsync(OnAfterRender)는 JS interop이라 Loose 모드로 둔다(컴포넌트가 try/catch로 보호하나 안전하게).
/// </summary>
public sealed class ModalAccessibilityTests
{
    private static TestContext NewCtx()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [Fact]
    public void NexaModal_renders_dialog_role_and_aria_label_from_title()
    {
        using var ctx = NewCtx();
        var cut = ctx.RenderComponent<NexaModal>(p => p
            .Add(m => m.Visible, true)
            .Add(m => m.Title, "설비 등록"));

        var panel = cut.Find(".nexa-modal-panel");
        panel.GetAttribute("role").Should().Be("dialog");
        panel.GetAttribute("aria-modal").Should().Be("true");

        // aria-labelledby가 제목 요소의 id와 연결돼야 한다(접근성 이름).
        var labelledBy = panel.GetAttribute("aria-labelledby");
        labelledBy.Should().NotBeNullOrEmpty();
        var title = cut.Find("h3");
        title.GetAttribute("id").Should().Be(labelledBy);
        title.TextContent.Should().Be("설비 등록");
    }

    [Fact]
    public void NexaModal_escape_closes_when_close_is_allowed()
    {
        using var ctx = NewCtx();
        bool? lastVisible = null;
        var cut = ctx.RenderComponent<NexaModal>(p => p
            .Add(m => m.Visible, true)
            .Add(m => m.Title, "닫기 허용 모달")
            .Add(m => m.ShowCloseButton, true)
            .Add(m => m.VisibleChanged, (bool v) => lastVisible = v));

        cut.Find(".nexa-modal-panel").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        lastVisible.Should().BeFalse("ShowCloseButton=true면 ESC가 모달을 닫아야 한다(VisibleChanged(false))");
    }

    [Fact]
    public void NexaModal_escape_does_not_close_when_close_is_disabled()
    {
        using var ctx = NewCtx();
        bool? lastVisible = null;
        var cut = ctx.RenderComponent<NexaModal>(p => p
            .Add(m => m.Visible, true)
            .Add(m => m.Title, "강제 선택 모달")
            .Add(m => m.ShowCloseButton, false)
            .Add(m => m.VisibleChanged, (bool v) => lastVisible = v));

        cut.Find(".nexa-modal-panel").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        lastVisible.Should().BeNull("ShowCloseButton=false(강제 선택)면 ESC로 닫히지 않아야 한다");
    }

    [Fact]
    public void ConfirmCloseDialog_renders_dialog_role_and_aria_label()
    {
        using var ctx = NewCtx();
        var cut = ctx.RenderComponent<ConfirmCloseDialog>(p => p
            .Add(d => d.Visible, true));

        var dialog = cut.Find(".confirm-close-dialog");
        dialog.GetAttribute("role").Should().Be("dialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        var labelledBy = dialog.GetAttribute("aria-labelledby");
        labelledBy.Should().NotBeNullOrEmpty();
        cut.Find(".confirm-close-title").GetAttribute("id").Should().Be(labelledBy);
    }

    [Fact]
    public void ConfirmCloseDialog_escape_maps_to_cancel()
    {
        using var ctx = NewCtx();
        CloseAction? action = null;
        var cut = ctx.RenderComponent<ConfirmCloseDialog>(p => p
            .Add(d => d.Visible, true)
            .Add(d => d.OnAction, (CloseAction a) => action = a));

        cut.Find(".confirm-close-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        action.Should().Be(CloseAction.Cancel, "강제 선택 다이얼로그에서 ESC는 데이터 유실 방지를 위해 '취소'여야 한다");
    }
}
