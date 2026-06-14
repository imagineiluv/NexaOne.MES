using NexaOne.Web.Services;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// DirtyTracker / WaitOverlayService 순수 카운팅 로직 회귀 안전망.
/// TestContext 없이 일반 xUnit — POCO 서비스이므로 실인스턴스로 직접 검증한다.
/// </summary>
public sealed class DirtyTrackerAndWaitOverlayTests
{
    // ───────────────────────── DirtyTracker ─────────────────────────

    [Fact]
    public void MarkDirty_같은_키_두_번이면_StateChanged는_1회만_발화한다()
    {
        var tracker = new DirtyTracker();
        var fired = 0;
        tracker.StateChanged += () => fired++;

        tracker.MarkDirty("k1");
        tracker.MarkDirty("k1");   // 이미 더티인 키 — HashSet.Add가 false → 재발화 없음

        fired.Should().Be(1, "동일 키를 다시 더티로 표시해도 상태 변화가 없으므로 이벤트는 한 번만 발화해야 한다");
        tracker.IsDirty.Should().BeTrue("키가 하나라도 더티면 IsDirty는 true여야 한다");
    }

    [Fact]
    public void MarkClean_두_키_중_한_키만_지우면_여전히_IsDirty_true다()
    {
        var tracker = new DirtyTracker();
        tracker.MarkDirty("k1");
        tracker.MarkDirty("k2");

        var fired = 0;
        tracker.StateChanged += () => fired++;

        tracker.MarkClean("k1");   // 두 키 중 하나만 정리

        tracker.IsDirty.Should().BeTrue("남은 더티 키가 있으므로 IsDirty는 여전히 true여야 한다");
        fired.Should().Be(1, "실제로 제거된 키 하나에 대해서만 StateChanged가 발화해야 한다");
    }

    [Fact]
    public void MarkClean_등록되지_않은_키는_StateChanged를_발화하지_않는다()
    {
        var tracker = new DirtyTracker();
        tracker.MarkDirty("k1");

        var fired = 0;
        tracker.StateChanged += () => fired++;

        tracker.MarkClean("없는키");   // HashSet.Remove가 false → 재발화 없음

        fired.Should().Be(0, "존재하지 않는 키 정리는 상태 변화가 없으므로 이벤트를 발화하지 않아야 한다");
        tracker.IsDirty.Should().BeTrue("k1이 그대로 남아 있으므로 여전히 더티여야 한다");
    }

    [Fact]
    public void ClearAll_이후_IsDirty는_false이고_StateChanged가_발화한다()
    {
        var tracker = new DirtyTracker();
        tracker.MarkDirty("k1");
        tracker.MarkDirty("k2");

        var fired = 0;
        tracker.StateChanged += () => fired++;

        tracker.ClearAll();

        tracker.IsDirty.Should().BeFalse("ClearAll 후에는 더티 키가 하나도 남지 않아야 한다");
        fired.Should().Be(1, "ClearAll은 비우는 작업이므로 StateChanged를 한 번 발화해야 한다");
    }

    // ───────────────────────── WaitOverlayService ─────────────────────────

    [Fact]
    public void Show_2회_중첩_후_1회_Dispose하면_여전히_IsVisible이고_2회째_Dispose에서_Message가_빈문자열이_된다()
    {
        var svc = new WaitOverlayService();

        var scope1 = svc.Show("첫 작업");
        var scope2 = svc.Show("둘째 작업");

        svc.IsVisible.Should().BeTrue("두 번 Show했으므로 오버레이는 표시 상태여야 한다");
        svc.Message.Should().Be("둘째 작업", "마지막 Show의 메시지가 현재 메시지여야 한다");

        scope1.Dispose();   // depth 2 → 1
        svc.IsVisible.Should().BeTrue("아직 해제되지 않은 작업이 하나 남았으므로 여전히 표시 상태여야 한다");
        svc.Message.Should().Be("둘째 작업", "depth가 0이 되기 전에는 메시지를 비우지 않아야 한다");

        scope2.Dispose();   // depth 1 → 0
        svc.IsVisible.Should().BeFalse("모든 작업이 해제되어 오버레이는 숨겨져야 한다");
        svc.Message.Should().Be(string.Empty, "depth가 0이 되면 메시지를 빈 문자열로 초기화해야 한다");
    }

    [Fact]
    public void Scope_이중_Dispose는_depth를_음수화하지_않고_중복_StateChanged를_발화하지_않는다()
    {
        var svc = new WaitOverlayService();
        var scope = svc.Show("작업");

        var fired = 0;
        svc.StateChanged += () => fired++;   // Show 이후 구독 — 해제 이벤트만 센다

        scope.Dispose();   // depth 1 → 0, 정상 해제 1회
        svc.IsVisible.Should().BeFalse("단일 Show를 해제했으므로 오버레이는 숨겨져야 한다");
        fired.Should().Be(1, "정상 Dispose는 StateChanged를 한 번 발화해야 한다");

        scope.Dispose();   // 동일 스코프 재해제 — _disposed 가드로 무시되어야 한다

        fired.Should().Be(1, "이미 Dispose된 스코프를 다시 Dispose해도 추가 StateChanged가 발화되면 안 된다");
        svc.IsVisible.Should().BeFalse("이중 Dispose가 depth를 음수화하면 안 되며 숨김 상태를 유지해야 한다");

        // depth 음수화 검증: 새 Show 1회면 정확히 IsVisible=true가 되어야 한다
        // (depth가 -1이었다면 Show 후에도 depth=0이라 보이지 않게 된다)
        using var probe = svc.Show("확인");
        svc.IsVisible.Should().BeTrue("이중 Dispose로 depth가 음수가 되지 않았다면 새 Show 1회로 다시 표시되어야 한다");
    }
}
