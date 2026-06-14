using NexaOne.Web.Services;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// MDI 탭 관리 순수 로직(MdiTabService) 회귀 안전망 — 설계서 §20.7.
/// POCO 서비스이므로 TestContext/JSInterop 불필요(일반 xUnit).
/// 중복 열기(dedup) · 파라미터 정규화 · LRU 축출 · 제목 요약 · 다음 활성 탭 · 정리 훅을 검증한다.
/// </summary>
public sealed class MdiTabServiceTests
{
    // ───────────────────────── (a) dedup: 동일 path + 동일 파라미터 재오픈 ─────────────────────────

    [Fact]
    public void Open_same_path_and_params_reuses_tab_and_activates_existing()
    {
        var svc = new MdiTabService();

        var first  = svc.Open("/mdm/plant", "?plantId=P1", "공장");
        var second = svc.Open("/mdm/plant", "?plantId=P1", "공장");

        ReferenceEquals(first, second).Should().BeTrue("동일 path+동일 파라미터 재오픈은 기존 탭을 반환해야 한다");
        svc.Tabs.Should().HaveCount(1, "중복 열기 정책상 새 탭이 생성되면 안 된다");
        svc.ActiveTab.Should().BeSameAs(first, "재오픈한 탭이 활성화돼야 한다");
        first.TabId.Should().Be(second.TabId, "dedup 키(tabId)는 path#hash로 동일해야 한다");
    }

    [Fact]
    public void Open_same_path_different_params_creates_new_tab()
    {
        var svc = new MdiTabService();

        svc.Open("/mdm/plant", "?plantId=P1", "공장");
        var other = svc.Open("/mdm/plant", "?plantId=P2", "공장");

        svc.Tabs.Should().HaveCount(2, "파라미터가 다르면 별도의 탭이 열려야 한다");
        svc.ActiveTab.Should().BeSameAs(other, "마지막에 연 탭이 활성화돼야 한다");
    }

    [Fact]
    public void Open_path_is_case_insensitive_in_tabId_but_param_order_irrelevant()
    {
        var svc = new MdiTabService();

        // 파라미터 순서만 다른 동일 조합 → 정규화 후 동일 해시 → 같은 탭.
        var a = svc.Open("/mdm/plant", "?a=1&b=2", "공장");
        var b = svc.Open("/mdm/plant", "?b=2&a=1", "공장");

        ReferenceEquals(a, b).Should().BeTrue("파라미터 키는 정렬 정규화되므로 순서가 달라도 동일 탭이어야 한다");
        svc.Tabs.Should().HaveCount(1);
    }

    [Fact]
    public void Open_with_no_query_sets_url_to_path_only()
    {
        var svc = new MdiTabService();

        var tab = svc.Open("/home", "", "홈");

        tab.Url.Should().Be("/home", "쿼리가 없으면 Url은 경로만이어야 한다");
        tab.MenuId.Should().Be("/home");
    }

    [Fact]
    public void Open_with_query_includes_query_in_url()
    {
        var svc = new MdiTabService();

        var tab = svc.Open("/mdm/plant", "?plantId=P1", "공장");

        tab.Url.Should().Be("/mdm/plant?plantId=P1", "쿼리가 있으면 Url에 그대로 포함돼야 한다");
    }

    // ───────────────────────── (b) NormalizeParameters: 날짜 ISO 정규화 · 키 대소문자 무시 ─────────────────────────

    [Fact]
    public void Open_date_param_date_only_and_iso_midnight_are_same_tab()
    {
        var svc = new MdiTabService();

        // '2026-06-10' 과 '2026-06-10T00:00:00' 은 ISO 정규화 후 동일 → 같은 탭.
        var dateOnly = svc.Open("/fdc/monitor", "?from=2026-06-10", "FDC 모니터");
        var isoMid   = svc.Open("/fdc/monitor", "?from=2026-06-10T00:00:00", "FDC 모니터");

        ReferenceEquals(dateOnly, isoMid).Should().BeTrue(
            "날짜성 파라미터는 ISO('O')로 정규화되어 '2026-06-10'='2026-06-10T00:00:00' 동일 탭이어야 한다");
        svc.Tabs.Should().HaveCount(1);
    }

    [Fact]
    public void Open_param_key_is_case_insensitive_for_dedup()
    {
        var svc = new MdiTabService();

        // 키 대소문자만 다른 경우 → 소문자화 정규화 → 동일 탭.
        var lower = svc.Open("/mdm/plant", "?plantid=P1", "공장");
        var upper = svc.Open("/mdm/plant", "?PlantId=P1", "공장");

        ReferenceEquals(lower, upper).Should().BeTrue("파라미터 키는 소문자로 정규화되므로 대소문자 차이는 동일 탭이어야 한다");
        svc.Tabs.Should().HaveCount(1);
    }

    [Fact]
    public void Open_different_date_values_create_different_tabs()
    {
        var svc = new MdiTabService();

        svc.Open("/fdc/monitor", "?from=2026-06-10", "FDC 모니터");
        svc.Open("/fdc/monitor", "?from=2026-06-11", "FDC 모니터");

        svc.Tabs.Should().HaveCount(2, "서로 다른 날짜값은 서로 다른 탭이어야 한다");
    }

    [Fact]
    public void Open_non_date_value_in_date_key_falls_back_to_raw_string()
    {
        var svc = new MdiTabService();

        // 파싱 불가한 'from' 값은 원문 그대로 사용되며, 동일 원문이면 동일 탭.
        var a = svc.Open("/fdc/monitor", "?from=notadate", "FDC 모니터");
        var b = svc.Open("/fdc/monitor", "?from=notadate", "FDC 모니터");

        ReferenceEquals(a, b).Should().BeTrue("날짜 파싱 실패 시 원문 문자열로 비교되어 동일 원문은 동일 탭이어야 한다");
        svc.Tabs.Should().HaveCount(1);
    }

    // ───────────────────────── (c) MaxTabs LRU 축출 ─────────────────────────

    [Fact]
    public void Eviction_removes_oldest_inactive_clean_idle_tab_at_max()
    {
        var svc = new MdiTabService();

        // MaxTabs(20)개를 서로 다른 파라미터로 연다. 마지막(20번째)이 활성.
        for (var i = 0; i < MdiTabService.MaxTabs; i++)
            svc.Open("/mdm/plant", $"?plantId=P{i}", "공장");

        svc.Tabs.Should().HaveCount(MdiTabService.MaxTabs);
        var oldest = svc.Tabs[0];   // P0 — 가장 먼저 활성화됨(LastActivatedSeq 최소), 현재 비활성

        // 21번째를 열면 가장 오래된 비활성·clean·idle 탭(P0)이 축출되고 총합은 MaxTabs 유지.
        svc.Open("/mdm/plant", "?plantId=NEW", "공장");

        svc.Tabs.Should().HaveCount(MdiTabService.MaxTabs, "축출이 일어나 최대 탭 수가 유지돼야 한다");
        svc.Tabs.Should().NotContain(oldest, "가장 오래된 비활성·non-dirty·non-busy 탭이 축출돼야 한다");
        svc.Tabs.Should().Contain(t => t.Url.Contains("plantId=NEW"), "새 탭은 추가돼야 한다");
    }

    [Fact]
    public void Eviction_never_evicts_dirty_busy_modal_or_active_tabs_and_allows_overflow()
    {
        var svc = new MdiTabService();

        var tabs = new List<MdiTab>();
        for (var i = 0; i < MdiTabService.MaxTabs; i++)
            tabs.Add(svc.Open("/mdm/plant", $"?plantId=P{i}", "공장"));

        // 비활성 탭 전부를 축출 불가 상태로 만든다(dirty/busy/modal). 활성 탭(마지막)은 본래 제외.
        // 결과적으로 축출 후보가 0개여야 한다.
        for (var i = 0; i < MdiTabService.MaxTabs; i++)
        {
            if (i % 3 == 0) tabs[i].IsDirty = true;
            else if (i % 3 == 1) tabs[i].IsBusy = true;
            else tabs[i].HasModalChild = true;
        }

        svc.Open("/mdm/plant", "?plantId=OVERFLOW", "공장");

        svc.Tabs.Should().HaveCount(MdiTabService.MaxTabs + 1,
            "축출 후보(non-dirty·non-busy·non-modal·non-active)가 없으면 초과를 허용해야 한다");
        svc.Tabs.Should().Contain(t => t.Url.Contains("plantId=OVERFLOW"));
    }

    [Fact]
    public void Eviction_skips_dirty_and_evicts_next_oldest_clean_tab()
    {
        var svc = new MdiTabService();

        var tabs = new List<MdiTab>();
        for (var i = 0; i < MdiTabService.MaxTabs; i++)
            tabs.Add(svc.Open("/mdm/plant", $"?plantId=P{i}", "공장"));

        var oldest       = tabs[0];   // 가장 오래된 — dirty로 보호
        var secondOldest = tabs[1];   // 그 다음 — clean이므로 축출 대상
        oldest.IsDirty = true;

        svc.Open("/mdm/plant", "?plantId=NEW", "공장");

        svc.Tabs.Should().Contain(oldest, "더티 탭은 가장 오래되어도 축출되면 안 된다");
        svc.Tabs.Should().NotContain(secondOldest, "더티 탭을 건너뛰고 그 다음으로 오래된 clean 탭이 축출돼야 한다");
        svc.Tabs.Should().HaveCount(MdiTabService.MaxTabs);
    }

    [Fact]
    public void Eviction_invokes_onclosed_hook_of_victim()
    {
        var svc = new MdiTabService();

        var tabs = new List<MdiTab>();
        for (var i = 0; i < MdiTabService.MaxTabs; i++)
            tabs.Add(svc.Open("/mdm/plant", $"?plantId=P{i}", "공장"));

        var victimClosed = false;
        tabs[0].OnClosed = () => victimClosed = true;   // 가장 오래된 = 축출 대상

        svc.Open("/mdm/plant", "?plantId=NEW", "공장");

        victimClosed.Should().BeTrue("축출(Remove 경유) 시 피해 탭의 OnClosed 정리 훅이 호출돼야 한다");
    }

    // ───────────────────────── (d) BuildTitle: SummaryKeys 요약 ─────────────────────────

    [Fact]
    public void Title_summarizes_configured_keys_for_summary_path()
    {
        var svc = new MdiTabService();

        var tab = svc.Open("/fdc/monitor", "?parameterId=PRM1&equipmentId=EQ001", "FDC 모니터");

        // SummaryKeys["/fdc/monitor"] = [parameterid, equipmentid] 순서로 요약.
        tab.Title.Should().Be("FDC 모니터 [PRM1, EQ001]",
            "SummaryKeys 경로는 설정 키 순서대로 제목에 [..]로 요약돼야 한다");
    }

    [Fact]
    public void Title_uses_summary_key_definition_order_not_query_order()
    {
        var svc = new MdiTabService();

        // 쿼리는 equipmentId 먼저지만, 제목은 SummaryKeys 정의 순서(parameterid, equipmentid)를 따른다.
        var tab = svc.Open("/fdc/monitor", "?equipmentId=EQ001&parameterId=PRM1", "FDC 모니터");

        tab.Title.Should().Be("FDC 모니터 [PRM1, EQ001]",
            "제목 요약은 쿼리 순서가 아니라 SummaryKeys 정의 순서를 따라야 한다");
    }

    [Fact]
    public void Title_uses_menu_name_only_for_non_summary_path()
    {
        var svc = new MdiTabService();

        var tab = svc.Open("/mdm/plant", "?plantId=P1", "공장 관리");

        tab.Title.Should().Be("공장 관리", "SummaryKeys 미등록 경로는 메뉴명만 제목으로 사용해야 한다");
    }

    [Fact]
    public void Title_uses_menu_name_only_when_summary_keys_missing_in_query()
    {
        var svc = new MdiTabService();

        // SummaryKeys 경로지만 해당 키가 쿼리에 없으면 메뉴명만.
        var tab = svc.Open("/fdc/monitor", "?other=X", "FDC 모니터");

        tab.Title.Should().Be("FDC 모니터", "요약 대상 키가 파라미터에 없으면 메뉴명만 표시해야 한다");
    }

    [Fact]
    public void Title_summary_key_is_case_insensitive()
    {
        var svc = new MdiTabService();

        // 쿼리 키가 대문자여도 정규화 후 요약돼야 한다(단일 키 경로로 검증).
        var tab = svc.Open("/est/alarms", "?EquipmentId=EQ777", "설비 알람");

        tab.Title.Should().Be("설비 알람 [EQ777]", "요약 키 매칭은 대소문자를 무시해야 한다(정규화는 소문자 키)");
    }

    // ───────────────────────── (e) NextAfterClose: 최근 활성 탭 ─────────────────────────

    [Fact]
    public void NextAfterClose_returns_most_recently_activated_other_tab()
    {
        var svc = new MdiTabService();

        var t1 = svc.Open("/mdm/plant", "?plantId=P1", "공장");
        var t2 = svc.Open("/mdm/plant", "?plantId=P2", "공장");
        var t3 = svc.Open("/mdm/plant", "?plantId=P3", "공장");

        // t1을 다시 활성화 → t1이 가장 최근 활성. 그 상태에서 활성 탭(t1) 닫기 시 다음은 t3(그 다음 최신).
        svc.Activate(t2);
        svc.Activate(t1);

        var next = svc.NextAfterClose(t1);

        next.Should().BeSameAs(t2, "닫는 탭을 제외하고 LastActivatedSeq가 가장 최신인 탭이 다음 활성 후보여야 한다");
    }

    [Fact]
    public void NextAfterClose_returns_null_when_closing_only_tab()
    {
        var svc = new MdiTabService();
        var only = svc.Open("/home", "", "홈");

        svc.NextAfterClose(only).Should().BeNull("유일한 탭을 닫으면 다음 활성 탭이 없어야 한다");
    }

    // ───────────────────────── (f) Remove/Clear: OnClosed 훅 ─────────────────────────

    [Fact]
    public void Remove_invokes_onclosed_and_clears_active_when_removing_active()
    {
        var svc = new MdiTabService();
        var tab = svc.Open("/home", "", "홈");

        var closed = false;
        tab.OnClosed = () => closed = true;

        svc.Remove(tab);

        closed.Should().BeTrue("Remove 시 OnClosed 정리 훅이 호출돼야 한다");
        svc.Tabs.Should().NotContain(tab, "Remove 후 탭 목록에서 제거돼야 한다");
        svc.ActiveTab.Should().BeNull("활성 탭을 제거하면 ActiveTab은 null이 돼야 한다");
    }

    [Fact]
    public void Clear_invokes_onclosed_for_all_tabs_and_resets_state()
    {
        var svc = new MdiTabService();
        var t1 = svc.Open("/mdm/plant", "?plantId=P1", "공장");
        var t2 = svc.Open("/mdm/plant", "?plantId=P2", "공장");

        var closedCount = 0;
        t1.OnClosed = () => closedCount++;
        t2.OnClosed = () => closedCount++;

        svc.Clear();

        closedCount.Should().Be(2, "Clear 시 모든 탭의 OnClosed 정리 훅이 호출돼야 한다");
        svc.Tabs.Should().BeEmpty("Clear 후 탭 목록은 비어야 한다");
        svc.ActiveTab.Should().BeNull("Clear 후 ActiveTab은 null이어야 한다");
    }

    // ───────────────────────── 부수: 이벤트/더티/저장 핸들러 ─────────────────────────

    [Fact]
    public void Open_raises_changed_event()
    {
        var svc = new MdiTabService();
        var raised = 0;
        svc.Changed += () => raised++;

        svc.Open("/home", "", "홈");

        raised.Should().BeGreaterThan(0, "탭을 열면 Changed 이벤트가 발생해 UI 갱신을 유발해야 한다");
    }

    [Fact]
    public void SetActiveDirty_toggles_only_on_change_and_reflects_display_title()
    {
        var svc = new MdiTabService();
        var tab = svc.Open("/mdm/plant", "?plantId=P1", "공장 관리");

        var raised = 0;
        svc.Changed += () => raised++;

        svc.SetActiveDirty(true);
        tab.IsDirty.Should().BeTrue();
        tab.DisplayTitle.Should().Be("공장 관리 *", "더티 탭 제목 뒤에는 '*'가 붙어야 한다");
        raised.Should().Be(1, "상태가 바뀔 때만 Changed가 발생해야 한다");

        svc.SetActiveDirty(true);   // 동일 값 → 변화 없음
        raised.Should().Be(1, "동일한 더티 상태 재설정 시 Changed가 발생하면 안 된다");
    }

    [Fact]
    public void DirtyTabs_reports_only_dirty_tabs()
    {
        var svc = new MdiTabService();
        var t1 = svc.Open("/mdm/plant", "?plantId=P1", "공장");
        svc.Open("/mdm/plant", "?plantId=P2", "공장");

        t1.IsDirty = true;

        svc.DirtyTabs.Should().ContainSingle().Which.Should().BeSameAs(t1, "DirtyTabs는 더티 탭만 반환해야 한다");
    }

    [Fact]
    public void RegisterSaveHandler_attaches_to_active_and_unregister_clears_it()
    {
        var svc = new MdiTabService();
        var tab = svc.Open("/mdm/plant", "?plantId=P1", "공장");

        Func<Task<bool>> handler = () => Task.FromResult(true);
        svc.RegisterSaveHandler(handler);
        tab.SaveHandler.Should().BeSameAs(handler, "저장 핸들러는 활성 탭에 등록돼야 한다");

        svc.UnregisterSaveHandler();
        tab.SaveHandler.Should().BeNull("UnregisterSaveHandler는 활성 탭의 핸들러를 해제해야 한다");
    }

    // ───────────────────────── IsTabbablePath ─────────────────────────

    [Theory]
    [InlineData("/auth/login", false)]
    [InlineData("/error/500", false)]
    [InlineData("/mdm/plant", true)]
    [InlineData("/home", true)]
    public void IsTabbablePath_excludes_auth_and_error_paths(string path, bool expected)
        => MdiTabService.IsTabbablePath(path).Should().Be(expected,
            "/auth, /error 경로는 탭 대상이 아니어야 한다");
}
