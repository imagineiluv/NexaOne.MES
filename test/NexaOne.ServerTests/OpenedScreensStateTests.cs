using FluentAssertions;
using NexaOne.Server.Services;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>MDI 탭 상태 — 드래그 순서 변경(Move, P3-10)의 순서/활성/통지 계약을 검증한다(no-DB 순수 단위).</summary>
public sealed class OpenedScreensStateTests
{
    private static OpenedScreensState WithTabs(params string[] uiIds)
    {
        var state = new OpenedScreensState();
        foreach (var ui in uiIds) state.Open(ui, ui);
        return state;
    }

    [Fact]
    public void Move_reorders_dragged_tab_before_target_and_notifies()
    {
        var state = WithTabs("A", "B", "C");
        var notified = false;
        state.Changed += () => notified = true;

        state.Move("C", "A");   // C를 A 위치로

        state.Screens.Select(s => s.UiId).Should().Equal("C", "A", "B");
        notified.Should().BeTrue();
    }

    [Fact]
    public void Move_keeps_active_tab_unchanged()
    {
        var state = WithTabs("A", "B", "C");   // 마지막 Open = C 활성
        state.Move("A", "C");                  // 오른쪽 이동 = 대상 슬롯 차지(대상 뒤로)
        state.ActiveUiId.Should().Be("C");     // 순서만 바뀌고 화면 전환 없음
        state.Screens.Select(s => s.UiId).Should().Equal("B", "C", "A");
    }

    [Fact]
    public void Move_ignores_unknown_or_same_tab()
    {
        var state = WithTabs("A", "B");
        var notified = false;
        state.Changed += () => notified = true;

        state.Move("A", "A");        // 자기 자신
        state.Move("X", "A");        // 드래그 탭 없음
        state.Move("A", "X");        // 대상 탭 없음

        state.Screens.Select(s => s.UiId).Should().Equal("A", "B");
        notified.Should().BeFalse();
    }
}
