using Bunit;
using Microsoft.AspNetCore.Components;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 메타 그리드 런타임 렌더러(Radzen DataGrid 기반). 그리드 렌더 자체는 라이브러리 책임이므로, 여기서는
/// 우리가 소유한 어댑터 로직(심각도 배지 매핑·타임스탬프 포맷)과 Radzen 그리드가 딕셔너리 행을
/// 예외 없이 렌더하는지(ExpandoObject 바인딩 회귀 가드)를 검증한다.
/// </summary>
public sealed class MetaGridRendererTests
{
    private static readonly GridColumnDefinition[] Columns =
    {
        new("LOGGED_AT", "발생시각"),
        new("LEVEL", "레벨"),
        new("SECRET", "숨김", Visible: false),
    };

    // ── 어댑터 로직(순수) ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Error", BadgeStyle.Danger)]
    [InlineData("Critical", BadgeStyle.Danger)]
    [InlineData("Warning", BadgeStyle.Warning)]
    [InlineData("Success", BadgeStyle.Success)]
    [InlineData("Active", BadgeStyle.Success)]
    [InlineData("Information", BadgeStyle.Info)]
    public void Known_severity_words_map_to_badge_style(string value, BadgeStyle expected)
        => MetaGridFormat.SeverityOf(value).Should().Be(expected);

    [Theory]
    [InlineData("PLANT01")]
    [InlineData("부산공장")]
    [InlineData("")]
    [InlineData("2026-07-04")]
    public void Unknown_values_render_plain_no_badge(string value)
        => MetaGridFormat.SeverityOf(value).Should().BeNull("알려지지 않은 값은 배지로 오탐하지 않아야 한다");

    [Fact]
    public void Iso_timestamp_is_humanized()
    {
        // 원시 ISO(소수점·Z)를 "yyyy-MM-dd HH:mm:ss" 로컬로 정형화한다(P1 — 원시 타임스탬프 제거).
        var formatted = MetaGridFormat.FormatCell("2026-07-04T12:30:44.0049805Z");
        formatted.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
        formatted.Should().NotContain("T").And.NotContain("Z");
    }

    [Fact]
    public void Non_timestamp_value_is_returned_verbatim()
        => MetaGridFormat.FormatCell("EQ01/TEMP 78.5").Should().Be("EQ01/TEMP 78.5");

    // ColumnKind는 internal이라 public 테스트 시그니처에 못 쓴다 → 결과를 문자열로 비교(본문은 InternalsVisibleTo로 접근).
    [Theory]
    [InlineData(new[] { "Warning", "Error", "Information" }, "Status")]
    [InlineData(new[] { "1", "0", "1" }, "Boolean")]                 // 0/1 → 불리언(숫자보다 우선)
    [InlineData(new[] { "100", "250", "" }, "Numeric")]              // 빈 값 섞여도 숫자
    [InlineData(new[] { "2026-07-04T12:30:44Z", "2026-07-05T00:00:00Z" }, "DateTime")]
    [InlineData(new[] { "", "", "" }, "Empty")]
    [InlineData(new[] { "P-1", "부산공장" }, "Text")]
    [InlineData(new[] { "100", "abc" }, "Text")]                     // 혼합 → 텍스트 안전 폴백
    public void InferKind_classifies_column_values(string[] values, string expected)
        => MetaGridFormat.InferKind(values).ToString().Should().Be(expected);

    [Fact]
    public void WidthFor_gives_narrow_types_fixed_width_and_flexes_text()
    {
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "100", "250" }), "QTY").Should().Be("108px", "숫자=고정 좁은 폭");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "", "" }), "NOTE").Should().Be("72px", "빈 컬럼=폭 축소");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "가공장", "나공장" }), "NAME").Should().BeNull("일반 텍스트=유연(남는 폭 분배)");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "MENU_A", "MENU_B" }), "MENU_ID").Should().Be("160px", "식별자 텍스트(_ID)=적당 폭(잘림 방지)");
    }

    // ── Radzen 렌더 스모크(딕셔너리→ExpandoObject 바인딩 회귀 가드) ──────────────

    private static TestContext RadzenContext()
    {
        var ctx = new TestContext();
        ctx.Services.AddRadzenComponents();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;   // Radzen JS 인터롭은 목킹(렌더 검증만)
        return ctx;
    }

    [Fact]
    public void Renders_dictionary_rows_without_throwing_and_shows_visible_columns()
    {
        using var ctx = RadzenContext();
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["LOGGED_AT"] = "2026-07-04T12:30:44Z", ["LEVEL"] = "Warning", ["SECRET"] = "hide" },
            new() { ["LOGGED_AT"] = "2026-07-04T12:31:00Z", ["LEVEL"] = "Error", ["SECRET"] = "hide2" },
        };

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, rows));

        // 딕셔너리 행이 ExpandoObject 바인딩으로 예외 없이 렌더되고, 보이는 컬럼 캡션이 나온다.
        cut.Markup.Should().Contain("발생시각").And.Contain("레벨");
        cut.Markup.Should().NotContain("숨김", "Visible=false 컬럼은 렌더되지 않아야 한다");
        cut.Markup.Should().Contain("Warning").And.Contain("Error");
        cut.Markup.Should().Contain("2 행");
    }

    [Fact]
    public void Loading_with_no_rows_renders_skeleton_not_text_spinner()
    {
        using var ctx = RadzenContext();
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)   // 2 보이는 컬럼
            .Add(c => c.Rows, (IReadOnlyList<Dictionary<string, object?>>?)null)
            .Add(c => c.Loading, true));

        cut.FindAll(".nx-skeleton").Should().NotBeEmpty("조회 중(행 없음)엔 스켈레톤 로더를 렌더해야 한다");
        cut.FindAll(".nx-skel-row").Count.Should().Be(7, "스켈레톤은 7개 shimmer 행");
        // 각 행의 셀 수 = 보이는 컬럼 수(2).
        cut.FindAll(".nx-skel-row").First().Children.Length.Should().Be(2, "셀 수는 보이는 컬럼 수와 일치");
    }

    [Fact]
    public void Empty_result_renders_styled_empty_state()
    {
        using var ctx = RadzenContext();
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, new List<Dictionary<string, object?>>()));   // 빈 결과(로딩 아님)

        cut.FindAll(".nx-grid-empty").Should().NotBeEmpty("빈 결과는 아이콘+문구 empty state로 렌더돼야 한다");
    }

    [Fact]
    public void Numeric_column_renders_tabular_cell_and_client_grid_shows_quick_filter()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("QTY", "수량"), new("NAME", "이름") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["QTY"] = "100", ["NAME"] = "가공장" },
            new() { ["QTY"] = "250", ["NAME"] = "나공장" },
        };

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        cut.FindAll(".nx-cell-num").Should().NotBeEmpty("숫자 컬럼 셀은 tabular 클래스로 렌더돼야 한다(타입 인지)");
        cut.FindAll("input.meta-grid-filter").Should().NotBeEmpty("클라이언트 화면은 빠른 필터 입력을 보여야 한다");
    }

    [Fact]
    public void Null_rows_shows_run_hint()
    {
        using var ctx = RadzenContext();
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, (IReadOnlyList<Dictionary<string, object?>>?)null));
        cut.Markup.Should().Contain("실행하면");
    }

    [Fact]
    public void Server_total_renders_custom_pager()
    {
        using var ctx = RadzenContext();
        var rows = Enumerable.Range(1, 20)
            .Select(i => new Dictionary<string, object?> { ["LOGGED_AT"] = $"t{i}", ["LEVEL"] = "Info" })
            .ToList();

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, rows)
            .Add(c => c.ServerTotal, 45)
            .Add(c => c.ServerPage, 0));

        // 서버측 페이징: 자체 페이저(총건수 + 이전/다음 + N/M).
        cut.Markup.Should().Contain("45 행").And.Contain("1 / 3");
    }

    // ── 그리드 표준 CRUD(추가/삭제/컬럼 필터) ───────────────────────────────────

    [Fact]
    public void Add_and_delete_buttons_render_only_when_enabled()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가" } };

        // 미지정(조회 전용) — 추가/삭제 버튼 없음.
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p.Add(c => c.Columns, cols).Add(c => c.Rows, rows));
        cut.Markup.Should().NotContain(">추가<").And.NotContain(">삭제<");

        // CRUD 켜짐 — 추가 렌더, 삭제는 선택 전 비활성.
        var added = false;
        var cut2 = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols).Add(c => c.Rows, rows)
            .Add(c => c.CanAdd, true).Add(c => c.OnAddNew, EventCallback.Factory.Create(this, () => added = true))
            .Add(c => c.CanDelete, true)
            .Add(c => c.OnDeleteRows, EventCallback.Factory.Create<List<Dictionary<string, object?>>>(this, _ => { })));
        cut2.Markup.Should().Contain("추가").And.Contain("삭제");
        var delBtn = cut2.FindAll("button").First(b => b.TextContent.Contains("삭제"));
        delBtn.HasAttribute("disabled").Should().BeTrue("선택 행이 없으면 삭제 비활성");

        // 추가 클릭 → 콜백.
        cut2.FindAll("button").First(b => b.TextContent.Contains("추가")).Click();
        added.Should().BeTrue();
    }

    [Fact]
    public void Delete_in_select_mode_emits_checked_rows()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["NAME"] = "가" }, new() { ["NAME"] = "나" }, new() { ["NAME"] = "다" },
        };
        List<Dictionary<string, object?>>? deleted = null;
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols).Add(c => c.Rows, rows)
            .Add(c => c.CanDelete, true)
            .Add(c => c.OnDeleteRows, EventCallback.Factory.Create<List<Dictionary<string, object?>>>(this, r => deleted = r)));

        // 선택 모드 진입 → 1·3행 체크 → 삭제 클릭 → 원본 행 딕셔너리 2건이 콜백된다.
        cut.FindAll(".meta-grid-toolbar button").First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "checklist").Click();
        var checks = cut.FindAll(".rz-data-row input[type=checkbox]");
        checks[0].Change(true);
        cut.FindAll(".rz-data-row input[type=checkbox]")[2].Change(true);
        cut.FindAll("button").First(b => b.TextContent.Contains("삭제")).Click();

        deleted.Should().NotBeNull();
        deleted!.Select(r => r["NAME"]).Should().BeEquivalentTo(new[] { "가", "다" });
    }

    [Fact]
    public void Bulk_commands_render_disabled_until_selection_and_emit_selected_rows()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("WORK_ORDER_ID", "W/O") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["WORK_ORDER_ID"] = "WO-1" }, new() { ["WORK_ORDER_ID"] = "WO-2" },
        };
        var cmds = new BulkCommandDefinition[] { new("확정", "POM.ReleaseWorkOrder") };
        (BulkCommandDefinition Command, List<Dictionary<string, object?>> Rows)? emitted = null;

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols).Add(c => c.Rows, rows)
            .Add(c => c.BulkCommands, cmds)
            .Add(c => c.OnBulkCommand,
                EventCallback.Factory.Create<(BulkCommandDefinition, List<Dictionary<string, object?>>)>(this, e => emitted = e)));

        // 렌더 + 선택 전 비활성.
        var btn = cut.FindAll(".meta-grid-toolbar button").First(b => b.TextContent.Contains("확정"));
        btn.HasAttribute("disabled").Should().BeTrue("선택 행이 없으면 일괄 명령 비활성");

        // 선택 모드 → 2행 체크 → 클릭 → (명령, 원본 행 2건) 방출.
        cut.FindAll(".meta-grid-toolbar button").First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "checklist").Click();
        var checks = cut.FindAll(".rz-data-row input[type=checkbox]");
        checks[0].Change(true);
        cut.FindAll(".rz-data-row input[type=checkbox]")[1].Change(true);
        cut.FindAll(".meta-grid-toolbar button").First(b => b.TextContent.Contains("확정")).Click();

        emitted.Should().NotBeNull();
        emitted!.Value.Command.CommandQueryId.Should().Be("POM.ReleaseWorkOrder");
        emitted.Value.Rows.Select(r => r["WORK_ORDER_ID"]).Should().BeEquivalentTo(new[] { "WO-1", "WO-2" });
    }

    [Fact]
    public void Column_filter_popup_applies_and_chips_show()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["NAME"] = "부산공장", ["QTY"] = "10" },
            new() { ["NAME"] = "서울공장", ["QTY"] = "20" },
            new() { ["NAME"] = "부산창고", ["QTY"] = "30" },
        };
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p.Add(c => c.Columns, cols).Add(c => c.Rows, rows));

        // 필터 팝업 열기 → NAME에 '부산' 입력 → 적용 → 2행만 남고 칩 노출.
        cut.FindAll(".meta-grid-toolbar button").First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "filter_alt").Click();
        cut.FindAll(".nx-filtermenu-row").Count.Should().Be(2, "보이는 컬럼마다 조건 행");
        cut.FindAll(".nx-filtermenu-val")[0].Input("부산");
        cut.FindAll(".nx-filtermenu-foot button").First().Click();

        cut.FindAll(".rz-data-row").Count.Should().Be(2, "포함 필터로 2행");
        cut.FindAll(".nx-filterchip").Count.Should().Be(1, "활성 필터 칩 1개");
        cut.Markup.Should().Contain("2 / 3 행");

        // 칩 × → 해제 → 전체 복원.
        cut.Find(".nx-filterchip .x").Click();
        cut.FindAll(".rz-data-row").Count.Should().Be(3);
    }

    // ── P1 안전장치(무제한 클라 로드 상한) 회귀 가드 ────────────────────────────

    [Fact]
    public void Client_grid_caps_rows_and_shows_truncation_banner_over_limit()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("LOT_ID", "LOT") };
        // 상한(MaxClientRows) 초과 결과 — 클라 페이징 화면이 전량을 회로에 올리지 않도록 잘라야 한다.
        var rows = Enumerable.Range(1, MetaGridRenderer.MaxClientRows + 25)
            .Select(i => new Dictionary<string, object?> { ["LOT_ID"] = $"L{i:D5}" })
            .ToList();

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));   // ServerTotal 없음 = 클라 페이징

        cut.FindAll(".nx-grid-trunc").Should().NotBeEmpty("상한 초과 시 절단 배너를 보여야 한다");
        // 원본 총수와 상한이 안내에 노출된다(전체 N행 중 처음 M행).
        cut.Markup.Should().Contain(rows.Count.ToString()).And.Contain(MetaGridRenderer.MaxClientRows.ToString());
    }

    [Fact]
    public void Client_grid_no_banner_when_within_limit()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("LOT_ID", "LOT") };
        var rows = Enumerable.Range(1, 30)
            .Select(i => new Dictionary<string, object?> { ["LOT_ID"] = $"L{i:D3}" })
            .ToList();

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        cut.FindAll(".nx-grid-trunc").Should().BeEmpty("상한 이내면 배너가 없어야 한다");
    }

    // ── 배치 B(UX 파워 기능) — 셀 툴팁·툴바 컨트롤·컬럼 선택기 ──────────────────

    [Fact]
    public void Cells_carry_title_tooltip_for_truncated_values()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["NAME"] = "아주 긴 설비 이름 값", ["QTY"] = "1234" },
        };
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        // 폭 제약으로 잘려도 호버로 읽을 수 있게 셀 span에 title이 붙는다(텍스트/숫자 공통).
        cut.Markup.Should().Contain("title=\"아주 긴 설비 이름 값\"");
        cut.Markup.Should().Contain("title=\"1234\"");
    }

    [Fact]
    public void Toolbar_renders_density_and_column_chooser_controls()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가", ["QTY"] = "1" } };
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        // 컬럼 선택기(view_column)·'더보기'(more_vert) 버튼이 툴바에 렌더된다.
        // 밀도 토글은 '더보기' 메뉴로 접혀(툴바 과밀 완화) 초기 마크업엔 없다 — 메뉴를 열면 나타난다.
        cut.Markup.Should().Contain("view_column").And.Contain("more_vert").And.NotContain("density_small");
        cut.FindAll(".nx-colmenu-wrap").Should().NotBeEmpty();
        // 초기엔 컬럼/더보기 메뉴 패널이 닫혀 있다.
        cut.FindAll(".nx-colmenu").Should().BeEmpty();

        // '더보기' 열기 → 밀도 토글 항목 노출.
        cut.FindAll(".meta-grid-toolbar button")
            .First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "more_vert").Click();
        cut.Markup.Should().Contain("density_small", "더보기 메뉴 안에 밀도 토글");
        cut.FindAll(".nx-moremenu-item").Should().NotBeEmpty();
    }

    [Fact]
    public void Column_chooser_hides_column_when_unchecked()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가공장", ["QTY"] = "1" } };
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        // 컬럼 메뉴 열기(view_column 버튼 — 필터 팝업도 colmenu-wrap을 쓰므로 아이콘으로 특정) → 체크박스 2개.
        cut.FindAll(".meta-grid-toolbar button").First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "view_column").Click();
        var checks = cut.FindAll(".nx-colmenu-item input[type=checkbox]");
        checks.Count.Should().Be(2, "보이는 컬럼마다 체크박스");
        cut.Markup.Should().Contain("가공장", "숨기기 전엔 QTY열과 함께 NAME 값이 보인다");

        // '이름' 컬럼 체크 해제 → 헤더에서 '이름' 캡션이 사라진다(QTY만 남음).
        cut.FindAll(".nx-colmenu-item input[type=checkbox]")[0].Change(false);
        cut.FindAll("th").Select(th => th.TextContent).Should()
            .NotContain(t => t.Contains("이름"), "숨긴 컬럼은 헤더에서 제외");
    }

    [Fact]
    public void Freeze_toggle_shown_only_on_wide_grids_and_applies_class()
    {
        using var ctx = RadzenContext();
        // 좁은 표(5열) — 고정 버튼 미노출.
        var narrow = new GridColumnDefinition[]
            { new("A","A"), new("B","B"), new("C","C"), new("D","D"), new("E","E") };
        var narrowRows = new List<Dictionary<string, object?>> { new() { ["A"]="1",["B"]="2",["C"]="3",["D"]="4",["E"]="5" } };
        // 고정 항목은 '더보기' 메뉴 안에 있다 — 좁은 표에선 메뉴를 열어도 미노출.
        var cutN = ctx.RenderComponent<MetaGridRenderer>(p => p.Add(c => c.Columns, narrow).Add(c => c.Rows, narrowRows));
        cutN.FindAll(".meta-grid-toolbar button")
            .First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "more_vert").Click();
        cutN.Markup.Should().NotContain("push_pin", "5열(좁은 표)에선 첫 컬럼 고정 항목을 숨긴다");

        // 넓은 표(6열) — 더보기 메뉴에 고정 항목 노출, 클릭 시 nx-freeze-first 클래스 적용.
        var wide = new GridColumnDefinition[]
            { new("A","A"), new("B","B"), new("C","C"), new("D","D"), new("E","E"), new("F","F") };
        var wideRows = new List<Dictionary<string, object?>> { new() { ["A"]="1",["B"]="2",["C"]="3",["D"]="4",["E"]="5",["F"]="6" } };
        var cutW = ctx.RenderComponent<MetaGridRenderer>(p => p.Add(c => c.Columns, wide).Add(c => c.Rows, wideRows));
        cutW.Find(".meta-grid").ClassList.Should().NotContain("nx-freeze-first");
        cutW.FindAll(".meta-grid-toolbar button")
            .First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "more_vert").Click();
        cutW.Markup.Should().Contain("push_pin", "6열(넓은 표)에선 더보기 메뉴에 첫 컬럼 고정 항목이 보인다");

        // 고정 항목 클릭 → 래퍼에 nx-freeze-first(토글류라 메뉴는 유지 — 라벨로 상태 확인).
        var freezeItem = cutW.FindAll(".nx-moremenu-item")
            .First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "push_pin");
        freezeItem.Click();
        cutW.Find(".meta-grid").ClassList.Should().Contain("nx-freeze-first", "고정 시 래퍼 클래스 적용");
        cutW.FindAll(".nx-moremenu").Should().NotBeEmpty("토글 후에도 더보기 메뉴 유지");
    }

    [Fact]
    public void Select_mode_shows_checkbox_column_and_bulk_bar_and_tracks_selection()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가" }, new() { ["NAME"] = "나" } };
        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p.Add(c => c.Columns, cols).Add(c => c.Rows, rows));

        // 초기(일반 모드): 일괄바·체크박스 없음, 선택 모드 토글 버튼은 있음.
        cut.Markup.Should().Contain("checklist");
        cut.FindAll(".nx-bulkbar").Should().BeEmpty();
        cut.FindAll(".rz-data-row input[type=checkbox]").Should().BeEmpty();

        // 선택 모드 진입 → 체크박스 컬럼 + 일괄 액션바.
        var selBtn = cut.FindAll(".meta-grid-toolbar button")
            .First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "checklist");
        selBtn.Click();
        cut.FindAll(".nx-bulkbar").Should().NotBeEmpty("선택 모드에서 일괄 액션바 노출");
        var rowChecks = cut.FindAll(".rz-data-row input[type=checkbox]");
        rowChecks.Count.Should().Be(2, "행마다 체크박스");
        cut.Find(".nx-bulkbar-count").TextContent.Trim().Should().StartWith("0", "초기 선택 0");

        // 한 행 선택 → 카운트 1.
        rowChecks[0].Change(true);
        cut.Find(".nx-bulkbar-count").TextContent.Trim().Should().StartWith("1", "행 선택 시 카운트 증가");
    }

    [Fact]
    public void Server_paged_grid_never_truncates_even_over_limit()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("LOT_ID", "LOT") };
        // 서버 페이징 화면은 이미 페이지 단위라 상한을 적용하지 않는다(ServerTotal 지정).
        var rows = Enumerable.Range(1, MetaGridRenderer.MaxClientRows + 10)
            .Select(i => new Dictionary<string, object?> { ["LOT_ID"] = $"L{i:D5}" })
            .ToList();

        var cut = ctx.RenderComponent<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows)
            .Add(c => c.ServerTotal, 99999)
            .Add(c => c.ServerPage, 0));

        cut.FindAll(".nx-grid-trunc").Should().BeEmpty("서버 페이징 화면은 절단 배너를 띄우지 않는다");
    }
}
