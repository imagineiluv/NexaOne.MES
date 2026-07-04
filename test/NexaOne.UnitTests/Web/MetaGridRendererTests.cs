using Bunit;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// Phase 4 — 메타데이터 주도 그리드 런타임 렌더러. ScreenDefinition.Columns(메타) + 쿼리 행(Dictionary)을
/// 받아: 보이는 컬럼만 캡션 헤더로 그리고, 각 행에서 컬럼 Key로 값을 찾아 셀에 매핑하는지 검증한다.
/// (파일 기반 쿼리 게이트웨이 결과를 손코딩 .razor 없이 렌더하는 저코드 조회 경로의 UI측)
/// </summary>
public sealed class MetaGridRendererTests
{
    private static readonly GridColumnDefinition[] Columns =
    {
        new("PLANT_ID", "공장 ID"),
        new("PLANT_NAME", "공장명"),
        new("SECRET", "숨김 컬럼", Visible: false),   // 보이지 않는 컬럼 — 헤더/셀 모두 제외돼야 한다.
    };

    private static IRenderedComponent<MetaGridRenderer> Render(
        TestContext ctx, IReadOnlyList<Dictionary<string, object?>>? rows)
        => ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, rows));

    [Fact]
    public void Renders_only_visible_columns_and_maps_row_values_by_key()
    {
        using var ctx = new TestContext();
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["PLANT_ID"] = "P-1", ["PLANT_NAME"] = "Plant One", ["SECRET"] = "hideme" },
            new() { ["PLANT_ID"] = "P-2", ["PLANT_NAME"] = "Plant Two", ["SECRET"] = "hideme2" },
        };

        var cut = Render(ctx, rows);

        // 보이는 컬럼 캡션만 헤더에 — 헤더 2개(숨김 컬럼 제외).
        cut.FindAll("thead th").Count.Should().Be(2, "Visible=false 컬럼은 헤더에서 제외돼야 한다");
        cut.Markup.Should().Contain("공장 ID").And.Contain("공장명");
        cut.Markup.Should().NotContain("숨김 컬럼", "보이지 않는 컬럼 캡션은 렌더되지 않아야 한다");

        // 행 2건, 각 행은 보이는 컬럼 수(2)만큼 셀 — Key로 값 매핑.
        cut.FindAll("tbody tr").Count.Should().Be(2);
        cut.FindAll("tbody tr")[0].QuerySelectorAll("td").Length.Should().Be(2);
        cut.Markup.Should().Contain("P-1").And.Contain("Plant One").And.Contain("P-2").And.Contain("Plant Two");
        cut.Markup.Should().NotContain("hideme", "보이지 않는 컬럼의 값은 셀로 렌더되지 않아야 한다");

        // 행 수 안내.
        cut.Markup.Should().Contain("2 행");
    }

    [Fact]
    public void Missing_key_renders_blank_cell_without_error()
    {
        using var ctx = new TestContext();
        // PLANT_NAME 키가 없는 행 — 해당 셀은 빈 칸이어야 한다(예외 없이).
        var rows = new List<Dictionary<string, object?>> { new() { ["PLANT_ID"] = "P-9" } };

        var cut = Render(ctx, rows);

        var cells = cut.FindAll("tbody tr td");
        cells.Count.Should().Be(2);
        cells[0].TextContent.Trim().Should().Be("P-9");
        cells[1].TextContent.Trim().Should().BeEmpty("매핑되는 키가 없는 셀은 빈 칸이어야 한다");
    }

    [Fact]
    public void Null_rows_shows_prompt_and_no_data_rows()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, rows: null);

        cut.FindAll("tbody tr").Should().BeEmpty("미실행(null) 상태에서는 데이터 행이 없어야 한다");
        cut.FindAll("thead th").Count.Should().Be(2, "헤더는 컬럼 메타로 항상 그려진다");
        cut.Markup.Should().Contain("실행하면 결과가 표시됩니다");
    }

    [Fact]
    public void Empty_rows_shows_no_result_message()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, rows: new List<Dictionary<string, object?>>());

        cut.FindAll("tbody tr").Should().BeEmpty();
        cut.Markup.Should().Contain("결과 없음");
    }

    [Fact]
    public void Column_width_meta_renders_fixed_colgroup_and_auto_for_unspecified()
    {
        // Phase-2 컬럼 폭 — Width(px) 지정 시 table-layout:fixed + colgroup 고정, 미지정 컬럼은 자동 분배.
        using var ctx = new TestContext();
        var columns = new GridColumnDefinition[]
        {
            new("PLANT_ID", "공장 ID", Width: 120),
            new("PLANT_NAME", "공장명"),                       // 폭 미지정 → 자동
            new("SECRET", "숨김", Visible: false, Width: 50),  // 숨김 컬럼은 colgroup에서도 제외
        };
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, columns)
            .Add(c => c.Rows, new List<Dictionary<string, object?>> { new() { ["PLANT_ID"] = "P-1" } }));

        cut.Find("table").ClassList.Should().Contain("fixed", "폭 지정 컬럼이 있으면 고정 레이아웃 클래스여야 한다(스타일은 CSS 단일 출처)");
        cut.FindAll("colgroup col").Count.Should().Be(2, "보이는 컬럼 수만큼 col(숨김 제외)");
        cut.Markup.Should().Contain("width:120px");
        cut.Markup.Should().NotContain("width:50px", "숨김 컬럼 폭은 렌더되지 않아야 한다");
    }

    [Fact]
    public void No_width_meta_keeps_auto_layout_with_bare_colgroup()
    {
        // P3-9 v3 — colgroup은 리사이즈 핸들이 <col>에 폭을 쓰도록 항상 렌더하되, 폭 미지정 컬럼은 bare <col>
        // (auto 레이아웃 불변). table.fixed 클래스는 실제 폭이 있을 때만 붙는다(하위호환).
        using var ctx = new TestContext();
        var cut = Render(ctx, rows: new List<Dictionary<string, object?>>());

        cut.Find("table").ClassList.Should().NotContain("fixed", "폭 미지정 화면은 기존 자동 레이아웃 유지(하위호환)");
        cut.Markup.Should().NotContain("width:", "폭 미지정이면 col에 width 스타일이 없어야 한다(auto 유지)");
    }

    // ── P3-9 — 클라이언트 정렬(숫자 인지)·페이징(20행/페이지) ────────────────

    private static List<Dictionary<string, object?>> IdRows(params string[] ids)
        => ids.Select(id => new Dictionary<string, object?> { ["PLANT_ID"] = id, ["PLANT_NAME"] = $"이름{id}" }).ToList();

    [Fact]
    public void Pages_rows_in_chunks_of_twenty_with_pager()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, IdRows(Enumerable.Range(1, 25).Select(i => $"P-{i:D2}").ToArray()));

        cut.FindAll("tbody tr").Count.Should().Be(20, "첫 페이지는 20행");
        cut.Markup.Should().Contain("25 행").And.Contain("1 / 2");

        cut.FindAll("button").First(b => b.TextContent.Contains("다음")).Click();
        cut.FindAll("tbody tr").Count.Should().Be(5, "둘째 페이지는 잔여 5행");
        cut.Markup.Should().Contain("2 / 2");
    }

    [Fact]
    public void Header_click_sorts_numerically_and_toggles_direction()
    {
        using var ctx = new TestContext();
        // 숫자 컬럼 — 문자열 정렬이면 "10"이 "2"보다 앞이므로 수치 정렬 여부가 구분된다.
        var cut = Render(ctx, IdRows("10", "2", "1"));

        cut.FindAll("thead th").First(t => t.TextContent.Contains("공장 ID")).Click();
        cut.FindAll("tbody tr")[0].TextContent.Should().Contain("이름1").And.NotContain("이름10", "오름차순 첫 행=1(수치 정렬)");
        cut.FindAll("tbody tr")[2].TextContent.Should().Contain("이름10");

        cut.FindAll("thead th").First(t => t.TextContent.Contains("공장 ID")).Click();
        cut.FindAll("tbody tr")[0].TextContent.Should().Contain("이름10", "재클릭=내림차순 토글");
    }

    [Fact]
    public void Small_result_shows_row_count_without_pager()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, IdRows("1", "2"));

        cut.Markup.Should().Contain("2 행");
        cut.FindAll("button").Should().BeEmpty("20행 이하는 페이저를 렌더하지 않는다");
    }

    [Fact]
    public void Server_paging_disables_client_slicing_and_delegates_page_change()
    {
        // P3-9 v2 — ServerTotal 지정 시: 행은 이미 한 페이지(분할 금지), 페이저는 서버 총건수 기준,
        // 페이지 전환은 OnServerPageChange 콜백으로 부모(MetaScreen)가 @offset 재조회한다.
        using var ctx = new TestContext();
        var requested = -1;
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, IdRows(Enumerable.Range(1, 20).Select(i => $"P-{i:D2}").ToArray()))
            .Add(c => c.ServerTotal, 45)
            .Add(c => c.ServerPage, 0)
            .Add(c => c.OnServerPageChange, (int page) => requested = page));

        cut.FindAll("tbody tr").Count.Should().Be(20, "서버 페이징은 받은 행을 그대로 렌더(클라 분할 없음)");
        cut.Markup.Should().Contain("45 행").And.Contain("1 / 3", "페이저는 서버 총건수 기준");

        cut.FindAll("button").First(b => b.TextContent.Contains("다음")).Click();
        requested.Should().Be(1, "다음 클릭=부모 콜백으로 페이지 요청(자체 상태 변경 없음)");

        cut.FindAll("button").First(b => b.TextContent.Contains("이전")).HasAttribute("disabled")
            .Should().BeTrue("첫 페이지에서 이전은 비활성");
    }

    [Fact]
    public void Server_paging_single_page_shows_total_without_pager()
    {
        using var ctx = new TestContext();
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, IdRows("1", "2"))
            .Add(c => c.ServerTotal, 2));

        cut.Markup.Should().Contain("2 행");
        cut.FindAll("button").Should().BeEmpty("총건수가 1페이지 이내면 서버 페이저를 렌더하지 않는다");
    }

    // ── P3-9 v3 컬럼 리사이즈 · P3-15 행 키보드 내비 (인터롭 마크업/무해 동작) ────

    [Fact]
    public void Renders_resize_handle_per_header_and_always_has_colgroup()
    {
        // P3-9 v3 — 헤더마다 리사이즈 핸들(.col-resize) + colgroup 상시 렌더(핸들이 <col>에 폭 기록).
        // JS(nxGridResizeInit)는 bUnit에 미탑재이나 OnAfterRender의 try/catch로 렌더는 정상이어야 한다.
        using var ctx = new TestContext();
        var cut = Render(ctx, IdRows("1", "2"));

        cut.FindAll("thead th .col-resize").Count.Should().Be(2, "보이는 헤더마다 리사이즈 핸들 1개");
        cut.FindAll("colgroup").Should().ContainSingle("리사이즈용 colgroup은 컬럼이 있으면 항상 렌더");
        cut.Find("table").ClassList.Should().NotContain("fixed", "폭 미지정이면 자동 레이아웃 유지");
    }

    [Fact]
    public void Selectable_grid_rows_are_keyboard_focusable_non_selectable_are_not()
    {
        // P3-15 — 선택 가능(OnRowSelect 배선) 그리드만 행 tabindex=0(방향키/Enter 대상).
        using var ctx = new TestContext();
        var selectable = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, IdRows("1", "2"))
            .Add(c => c.OnRowSelect, (Dictionary<string, object?> _) => { }));
        selectable.FindAll("tbody tr[tabindex=\"0\"]").Count.Should().Be(2, "선택 가능 그리드 행은 포커스 가능해야 한다");

        var plain = Render(ctx, IdRows("1", "2"));
        plain.FindAll("tbody tr[tabindex=\"0\"]").Should().BeEmpty("선택 콜백이 없으면 행에 tabindex를 주지 않는다");
    }

    [Fact]
    public void Row_keydown_selects_on_enter_without_js()
    {
        // Enter=선택은 JS 없이 동작(방향키만 JS 포커스 이동). 선택 콜백이 호출되고 행이 하이라이트돼야 한다.
        using var ctx = new TestContext();
        Dictionary<string, object?>? picked = null;
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, IdRows("1", "2"))
            .Add(c => c.OnRowSelect, (Dictionary<string, object?> r) => picked = r));

        cut.FindAll("tbody tr")[1].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        picked.Should().NotBeNull("Enter는 JS 없이 행을 선택해야 한다");
        picked!["PLANT_ID"].Should().Be("2");
        cut.FindAll("tbody tr.selected").Should().ContainSingle();
    }
}
