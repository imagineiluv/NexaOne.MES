using Bunit;
using FluentAssertions;
using NexaOne.Server.Components;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>사이드바 트리(MesNavTree) 렌더 스모크 — 953줄 셸 스택의 첫 bUnit 안전망.
/// 핵심은 '준비 중' 판정 3분법: ①메타 정의 등록=점등 ②호스트 리터럴 라우트(@page "/meta/리터럴")=점등
/// ③둘 다 아니면 pending(감쇠+배지). ②를 놓치면 동작하는 전용 화면 4종이 회색 오표시되는 실사고가 있었다.</summary>
public sealed class MesNavTreeRenderTests
{
    private static MesMenuNode Leaf(string id, string name, string uiId)
        => new(id, name, null, 1, "Screen", uiId, Array.Empty<MesMenuNode>());

    private static MesMenuNode Folder(string id, string name, params MesMenuNode[] children)
        => new(id, name, null, 1, "Folder", "", children);

    private IRenderedComponent<MesNavTree> Render(
        TestContext ctx,
        IReadOnlySet<string> knownScreenUiIds,
        params MesMenuNode[] nodes)
        => ctx.RenderComponent<MesNavTree>(p => p
            .Add(c => c.Nodes, nodes)
            .Add(c => c.Open, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .Add(c => c.KnownScreenUiIds, knownScreenUiIds));

    private static IReadOnlySet<string> Known(params string[] uiIds)
        => uiIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Registered_screen_renders_lit_and_unknown_renders_pending()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, Known("LIT_SCREEN"),
            Leaf("M1", "점등 화면", "LIT_SCREEN"),
            Leaf("M2", "미점등 화면", "NO_SUCH_SCREEN"));

        var links = cut.FindAll("a.mes-nav-subitem");
        links.Should().HaveCount(2);
        links[0].ClassList.Should().NotContain("pending", "메타 정의가 등록된 잎은 점등");
        links[1].ClassList.Should().Contain("pending", "정의도 리터럴 라우트도 없는 잎은 준비 중");
        links[1].GetAttribute("title").Should().Contain("준비 중");
    }

    [Fact]
    public void Host_literal_route_screen_is_not_marked_pending()
    {
        // SYS_USER_REQUESTS는 HostUserRequests.razor의 @page "/meta/SYS_USER_REQUESTS" — 메타 정의가
        // 없어도 동작하는 화면이다. 이 케이스가 pending으로 표시되면 실사고 재발(회귀 가드).
        using var ctx = new TestContext();
        var cut = Render(ctx, Known(), Leaf("M1", "사용자 신청 승인", "SYS_USER_REQUESTS"));

        cut.Find("a.mes-nav-subitem").ClassList.Should().NotContain("pending",
            "호스트 리터럴 라우트(@page /meta/리터럴)는 점등으로 집계해야 한다");
    }

    [Fact]
    public void Folder_toggle_is_keyboard_activatable_and_renders_children()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, Known("CHILD_SCREEN"),
            Folder("F1", "폴더", Leaf("M1", "자식", "CHILD_SCREEN")));

        var head = cut.Find(".mes-folder-head");
        head.GetAttribute("role").Should().Be("button");
        head.GetAttribute("tabindex").Should().Be("0");
        cut.Markup.Should().Contain("자식", "자식 잎이 재귀 렌더돼야 한다");
    }

    [Fact]
    public void Display_name_delegate_applies_current_language_to_folder_and_screen_labels()
    {
        using var ctx = new TestContext();
        var cut = ctx.RenderComponent<MesNavTree>(parameters => parameters
            .Add(component => component.Nodes, new[]
            {
                Folder("FACTORY_QCA", "품질검사", Leaf("INCOMING", "수입검사", "INCOMING"))
            })
            .Add(component => component.Open, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FACTORY_QCA"
            })
            .Add(component => component.KnownScreenUiIds, Known("INCOMING"))
            .Add(component => component.DisplayName, node => node.Id switch
            {
                "FACTORY_QCA" => "Quality Inspection",
                "INCOMING" => "Incoming Inspection",
                _ => node.Name,
            }));

        cut.Markup.Should().Contain("Quality Inspection");
        cut.Markup.Should().Contain("Incoming Inspection");
        cut.Markup.Should().NotContain(">품질검사<");
    }
}
