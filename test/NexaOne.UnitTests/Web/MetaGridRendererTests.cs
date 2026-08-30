using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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
    public void WidthFor_reserves_a_stable_minimum_and_expands_for_localized_headers()
    {
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "100", "250" }), "QTY").Should().Be("108px", "숫자=고정 좁은 폭");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "", "" }), "NOTE").Should().Be("72px", "빈 컬럼=폭 축소");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "가공장", "나공장" }), "NAME").Should().Be("140px", "일반 텍스트도 헤더가 찌그러지지 않는 최소 폭을 가진다");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "MENU_A", "MENU_B" }), "MENU_ID").Should().Be("160px", "식별자 텍스트(_ID)=적당 폭(잘림 방지)");
        MetaGridFormat.WidthFor(MetaGridFormat.InferKind(new[] { "100", "250" }), "CURRENT_QTY", "Current Quantity")
            .Should().Be("160px", "현재 언어의 긴 헤더는 표 내부 폭을 늘려 전체 문서가 아닌 data viewport에서 스크롤된다");
    }

    [Fact]
    public void Rendered_columns_apply_text_minimum_and_localized_caption_widths()
    {
        using var ctx = RadzenContext();
        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, new GridColumnDefinition[]
            {
                new("WAREHOUSE", "Warehouse"),
                new("CURRENT_QTY", "Current Quantity"),
            })
            .Add(component => component.Rows, new List<Dictionary<string, object?>>
            {
                new() { ["WAREHOUSE"] = "WH-A", ["CURRENT_QTY"] = 120 },
            }));

        cut.FindAll("col").Select(column => column.GetAttribute("style")).Should()
            .Contain(style => style != null && style.Contains("width:140px", StringComparison.OrdinalIgnoreCase))
            .And.Contain(style => style != null && style.Contains("width:160px", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Card_summary_policy_keeps_status_and_business_context_ahead_of_low_value_columns()
    {
        var columns = new GridColumnDefinition[]
        {
            new("ORDER_ID", "오더 ID"),
            new("DESCRIPTION", "설명"),
            new("REMARK", "비고"),
            new("CREATED_BY", "등록자"),
            new("UPDATED_BY", "수정자"),
            new("CUSTOMER_ID", "고객"),
            new("PRODUCT_ID", "품목"),
            new("DUE_DATE", "납기"),
            new("PLAN_QTY", "계획수량"),
            new("STATUS", "상태"),
        };

        var primary = MetaGridColumnPolicy.CardPrimary(columns);
        var summary = MetaGridColumnPolicy.CardSummary(columns, primary);

        primary!.Key.Should().Be("ORDER_ID");
        summary.Select(column => column.Key).Should().Equal(
            "STATUS", "CUSTOMER_ID", "PRODUCT_ID", "DUE_DATE", "PLAN_QTY", "DESCRIPTION");
    }

    [Fact]
    public void Card_summary_policy_is_stable_and_honours_the_shared_field_limit()
    {
        var columns = Enumerable.Range(1, 10)
            .Select(index => new GridColumnDefinition($"FIELD_{index}", $"필드 {index}"))
            .ToArray();

        var primary = MetaGridColumnPolicy.CardPrimary(columns);
        var summary = MetaGridColumnPolicy.CardSummary(columns, primary);

        primary!.Key.Should().Be("FIELD_1");
        summary.Should().HaveCount(MetaGridColumnPolicy.DefaultCardFieldCount);
        summary.Select(column => column.Key).Should().Equal(
            "FIELD_2", "FIELD_3", "FIELD_4", "FIELD_5", "FIELD_6", "FIELD_7");
    }

    // ── Radzen 렌더 스모크(딕셔너리→ExpandoObject 바인딩 회귀 가드) ──────────────

    private static BunitContext RadzenContext()
    {
        var ctx = new BunitContext();
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

        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, Columns)
            .Add(c => c.Rows, rows));

        // 딕셔너리 행이 ExpandoObject 바인딩으로 예외 없이 렌더되고, 보이는 컬럼 캡션이 나온다.
        cut.Markup.Should().Contain("발생시각").And.Contain("레벨");
        cut.Markup.Should().NotContain("숨김", "Visible=false 컬럼은 렌더되지 않아야 한다");
        cut.Markup.Should().Contain("Warning").And.Contain("Error");
        cut.Markup.Should().Contain("2 행");
    }

    [Fact]
    public void English_mode_uses_field_keys_when_a_domain_caption_resource_is_not_yet_seeded()
    {
        using var ctx = RadzenContext();
        var ui = new NexaOne.Web.Services.UiTextService();
        ui.Load("EnUs", new Dictionary<string, string>
        {
            ["common.rowsUnit"] = "rows",
            ["field.LEVEL"] = "Severity",
        });
        ctx.Services.AddSingleton(ui);

        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, Columns)
            .Add(component => component.Rows, new List<Dictionary<string, object?>>
            {
                new() { ["LOGGED_AT"] = "2026-07-04T12:30:44Z", ["LEVEL"] = "Warning" },
            }));

        cut.Markup.Should().Contain("Logged At", "unseeded field keys still need a readable English label")
            .And.Contain("Severity", "an explicit domain translation must override automatic humanizing")
            .And.NotContain("발생시각")
            .And.NotContain("레벨");
    }

    [Fact]
    public void Loading_with_no_rows_renders_skeleton_not_text_spinner()
    {
        using var ctx = RadzenContext();
        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        var cut = ctx.Render<MetaGridRenderer>(p => p
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

        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        cut.FindAll(".nx-cell-num").Should().NotBeEmpty("숫자 컬럼 셀은 tabular 클래스로 렌더돼야 한다(타입 인지)");
        cut.FindAll("input.meta-grid-filter").Should().NotBeEmpty("클라이언트 화면은 빠른 필터 입력을 보여야 한다");
    }

    [Fact]
    public void Null_rows_shows_run_hint()
    {
        using var ctx = RadzenContext();
        var cut = ctx.Render<MetaGridRenderer>(p => p
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

        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        var cut = ctx.Render<MetaGridRenderer>(p => p.Add(c => c.Columns, cols).Add(c => c.Rows, rows));
        cut.Markup.Should().NotContain(">추가<").And.NotContain(">삭제<");

        // CRUD 켜짐 — 추가 렌더, 삭제는 선택 전 비활성.
        var added = false;
        var cut2 = ctx.Render<MetaGridRenderer>(p => p
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
        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        var cmds = new BulkCommandDefinition[] { new("확정", "TEST.Release") };
        (BulkCommandDefinition Command, List<Dictionary<string, object?>> Rows)? emitted = null;

        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        emitted!.Value.Command.CommandQueryId.Should().Be("TEST.Release");
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
        var cut = ctx.Render<MetaGridRenderer>(p => p.Add(c => c.Columns, cols).Add(c => c.Rows, rows));

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

    [Fact]
    public void Sort_header_announces_current_direction_and_next_action()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["NAME"] = "나" },
            new() { ["NAME"] = "가" },
        };
        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        cut.Find(".nx-sort").GetAttribute("aria-label")
            .Should().Contain("정렬 안 됨").And.Contain("오름차순 정렬");

        cut.Find(".nx-sort").Click();
        cut.Find(".nx-sort").GetAttribute("aria-label")
            .Should().Contain("현재 오름차순").And.Contain("내림차순으로 변경");

        cut.Find(".nx-sort").Click();
        cut.Find(".nx-sort").GetAttribute("aria-label")
            .Should().Contain("현재 내림차순").And.Contain("오름차순으로 변경");
    }

    [Fact]
    public void Filter_popover_is_named_focusable_and_escape_closes_it()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가", ["QTY"] = 1 } };
        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        cut.FindAll(".meta-grid-toolbar button")
            .First(button => button.TextContent.Contains("filter_alt")).Click();

        var panel = cut.Find(".nx-filtermenu");
        panel.GetAttribute("role").Should().Be("dialog");
        panel.GetAttribute("aria-modal").Should().Be("false", "컬럼 필터는 배경을 잠그지 않는 비모달 팝오버다");
        panel.GetAttribute("tabindex").Should().Be("-1", "열린 뒤 패널로 프로그래밍 방식 포커스를 이동한다");
        cut.FindAll(".nx-filtermenu-op").Select(select => select.GetAttribute("aria-label"))
            .Should().Equal("이름 필터 방식", "수량 필터 방식");

        panel.KeyDown("Escape");
        cut.FindAll(".nx-filtermenu").Should().BeEmpty("Escape는 현재 팝오버만 닫아야 한다");
        ctx.JSInterop.Invocations.Count(invocation => invocation.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
            .Should().BeGreaterThanOrEqualTo(2, "열 때 패널로, 닫을 때 원래 트리거로 포커스를 이동해야 한다");
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

        var cut = ctx.Render<MetaGridRenderer>(p => p
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

        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        var cut = ctx.Render<MetaGridRenderer>(p => p
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
    public void Icon_only_grid_tools_have_localized_accessible_names()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가", ["QTY"] = "1" } };
        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        var toolButtons = cut.FindAll(".meta-grid-toolbar button");
        toolButtons.First(button => button.TextContent.Contains("filter_alt"))
            .GetAttribute("aria-label").Should().Be("컬럼 필터");
        toolButtons.First(button => button.TextContent.Contains("checklist"))
            .GetAttribute("aria-label").Should().Be("선택 모드");
        toolButtons.First(button => button.TextContent.Contains("view_column"))
            .GetAttribute("aria-label").Should().Be("컬럼 선택");
        toolButtons.First(button => button.TextContent.Contains("more_vert"))
            .GetAttribute("aria-label").Should().Be("더보기");
    }

    [Fact]
    public void Uppercase_business_status_uses_localized_semantic_badge_and_preserves_raw_value()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("ORDER_STATUS", "상태") };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ORDER_STATUS"] = "DRAFT" },
            new() { ["ORDER_STATUS"] = "CONFIRMED" },
            new() { ["ORDER_STATUS"] = "PRODUCING" },
            new() { ["ORDER_STATUS"] = "DELIVERED" },
            new() { ["ORDER_STATUS"] = "CLOSED" },
            new() { ["ORDER_STATUS"] = "CUSTOM_STATE" },
        };
        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows));

        var badges = cut.FindAll(".nx-status-badge");
        badges.Select(badge => badge.TextContent.Trim()).Should()
            .Equal("초안", "확정", "생산 중", "납품 완료", "마감", "CUSTOM_STATE");
        badges.Select(badge => badge.GetAttribute("title")).Should()
            .Equal(
                new[] { "DRAFT", "CONFIRMED", "PRODUCING", "DELIVERED", "CLOSED", "CUSTOM_STATE" },
                "표시 라벨만 현지화하고 원본 계약 값은 title로 보존해야 한다");
        badges[0].ClassList.Should().Contain("rz-badge-info", "초안은 정보 tone");
        badges[1].ClassList.Should().Contain("rz-badge-info", "확정은 정보 tone");
        badges[2].ClassList.Should().Contain("rz-badge-success", "생산 중은 정상 진행 tone");
        badges[3].ClassList.Should().Contain("rz-badge-success", "납품 완료는 성공 tone");
        badges[4].ClassList.Should().Contain("rz-badge-light", "마감은 중성 tone");
        badges.Last().ClassList.Should().Contain("rz-badge-light", "미정 상태는 중성 tone으로 폴백해야 한다");
    }

    [Fact]
    public void English_business_status_labels_remain_natural_without_seeded_status_resources()
    {
        using var ctx = RadzenContext();
        var ui = new NexaOne.Web.Services.UiTextService();
        ui.Load("EnUs", new Dictionary<string, string>());
        ctx.Services.AddSingleton(ui);
        var statuses = new[] { "Draft", "Confirmed", "Producing", "Delivered", "Closed" };
        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, new GridColumnDefinition[] { new("STATUS", "상태") })
            .Add(component => component.Rows, statuses
                .Select(status => new Dictionary<string, object?> { ["STATUS"] = status })
                .ToList()));

        cut.FindAll(".nx-status-badge").Select(badge => badge.TextContent.Trim()).Should()
            .Equal(statuses);
    }

    [Fact]
    public void Column_chooser_hides_column_when_unchecked()
    {
        using var ctx = RadzenContext();
        var cols = new GridColumnDefinition[] { new("NAME", "이름"), new("QTY", "수량") };
        var rows = new List<Dictionary<string, object?>> { new() { ["NAME"] = "가공장", ["QTY"] = "1" } };
        var cut = ctx.Render<MetaGridRenderer>(p => p
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
        var cutN = ctx.Render<MetaGridRenderer>(p => p.Add(c => c.Columns, narrow).Add(c => c.Rows, narrowRows));
        cutN.FindAll(".meta-grid-toolbar button")
            .First(b => b.QuerySelector(".rzi")?.TextContent.Trim() == "more_vert").Click();
        cutN.Markup.Should().NotContain("push_pin", "5열(좁은 표)에선 첫 컬럼 고정 항목을 숨긴다");

        // 넓은 표(6열) — 더보기 메뉴에 고정 항목 노출, 클릭 시 nx-freeze-first 클래스 적용.
        var wide = new GridColumnDefinition[]
            { new("A","A"), new("B","B"), new("C","C"), new("D","D"), new("E","E"), new("F","F") };
        var wideRows = new List<Dictionary<string, object?>> { new() { ["A"]="1",["B"]="2",["C"]="3",["D"]="4",["E"]="5",["F"]="6" } };
        var cutW = ctx.Render<MetaGridRenderer>(p => p.Add(c => c.Columns, wide).Add(c => c.Rows, wideRows));
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
        var cut = ctx.Render<MetaGridRenderer>(p => p.Add(c => c.Columns, cols).Add(c => c.Rows, rows));

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

        var cut = ctx.Render<MetaGridRenderer>(p => p
            .Add(c => c.Columns, cols)
            .Add(c => c.Rows, rows)
            .Add(c => c.ServerTotal, 99999)
            .Add(c => c.ServerPage, 0));

        cut.FindAll(".nx-grid-trunc").Should().BeEmpty("서버 페이징 화면은 절단 배너를 띄우지 않는다");
    }

    [Fact]
    public void View_segments_expose_four_accessible_modes_only_when_enabled()
    {
        using var ctx = RadzenContext();
        var rows = new List<Dictionary<string, object?>> { new() { ["ORDER_ID"] = "WO-1" } };
        var columns = new GridColumnDefinition[] { new("ORDER_ID", "작업지시") };

        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, rows)
            .Add(component => component.ScreenKey, "MANAGE_VIEW")
            .Add(component => component.EnableViewModes, true));

        var buttons = cut.FindAll(".nx-view-segments button");
        buttons.Should().HaveCount(4);
        buttons.Count(button => button.GetAttribute("aria-pressed") == "true").Should().Be(1);
        buttons.Should().OnlyContain(button => button.GetAttribute("aria-controls") == cut.Find(".meta-grid-content").Id);
        buttons.Should().OnlyContain(button => button.TagName == "BUTTON" && button.GetAttribute("type") == "button",
            "네이티브 버튼은 Enter/Space 키보드 선택을 기본 제공한다");

        var expected = new[] { "standard-table", "dense-table", "card", "split-detail" };
        foreach (var mode in expected)
        {
            var button = cut.FindAll(".nx-view-segments button")[Array.IndexOf(expected, mode)];
            button.Click();
            cut.Find(".meta-grid").GetAttribute("data-view").Should().Be(mode);
            cut.FindAll(".nx-view-segments button").Count(item => item.GetAttribute("aria-pressed") == "true").Should().Be(1);
        }

        var disabled = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, rows));
        disabled.FindAll(".meta-grid-viewbar").Should().BeEmpty();
    }

    [Fact]
    public void View_preference_restores_with_new_key_priority_saves_and_reloads_on_screen_change()
    {
        using var ctx = RadzenContext();
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "nxgrid:MANAGE_A:density").SetResult("compact");
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "nxgrid:MANAGE_A:view").SetResult("card");
        ctx.JSInterop.Setup<string?>("localStorage.getItem", "nxgrid:MANAGE_B:view").SetResult("not-a-view");
        var rows = new List<Dictionary<string, object?>> { new() { ["ORDER_ID"] = "WO-1" } };
        var columns = new GridColumnDefinition[] { new("ORDER_ID", "작업지시") };

        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, rows)
            .Add(component => component.ScreenKey, "MANAGE_A")
            .Add(component => component.EnableViewModes, true));

        cut.WaitForAssertion(() => cut.Find(".meta-grid").GetAttribute("data-view").Should().Be("card",
            "신규 view 키가 기존 compact density 마이그레이션보다 우선한다"));
        cut.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("분할 상세")).Click();
        ctx.JSInterop.Invocations.Should().Contain(invocation =>
            invocation.Identifier == "localStorage.setItem"
            && invocation.Arguments.Count == 2
            && Convert.ToString(invocation.Arguments[0]) == "nxgrid:MANAGE_A:view"
            && Convert.ToString(invocation.Arguments[1]) == "split-detail");

        cut.Render(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, rows)
            .Add(component => component.ScreenKey, "MANAGE_B")
            .Add(component => component.EnableViewModes, true));
        cut.WaitForAssertion(() => cut.Find(".meta-grid").GetAttribute("data-view").Should().Be("standard-table",
            "화면 키 전환 시 이전 모드를 누출하지 않고 잘못된 저장값은 표준 표로 안전하게 폴백한다"));
    }

    [Fact]
    public void View_switch_preserves_filter_sort_selection_and_crud_contracts()
    {
        using var ctx = RadzenContext();
        var columns = new GridColumnDefinition[]
        {
            new("ORDER_ID", "작업지시"),
            new("NAME", "품목명"),
            new("STATUS", "상태"),
            new("SECRET", "숨김", Visible: false),
        };
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ORDER_ID"] = "WO-1", ["NAME"] = "Busan B", ["STATUS"] = "DRAFT", ["SECRET"] = "S1" },
            new() { ["ORDER_ID"] = "WO-2", ["NAME"] = "Busan A", ["STATUS"] = "CONFIRMED", ["SECRET"] = "S2" },
            new() { ["ORDER_ID"] = "WO-3", ["NAME"] = "Seoul A", ["STATUS"] = "STARTED", ["SECRET"] = "S3" },
        };
        Dictionary<string, object?>? selected = null;
        List<Dictionary<string, object?>>? deleted = null;
        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, rows)
            .Add(component => component.ScreenKey, "MANAGE_STATE")
            .Add(component => component.EnableViewModes, true)
            .Add(component => component.CanDelete, true)
            .Add(component => component.OnRowSelect,
                EventCallback.Factory.Create<Dictionary<string, object?>>(this, row => selected = row))
            .Add(component => component.OnDeleteRows,
                EventCallback.Factory.Create<List<Dictionary<string, object?>>>(this, result => deleted = result)));

        cut.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("카드")).Click();
        cut.Find(".nx-view-sort select").Change("NAME");
        cut.FindAll(".meta-grid-toolbar button").First(button => button.TextContent.Contains("filter_alt")).Click();
        cut.FindAll(".nx-filtermenu-val")[1].Input("Busan");
        cut.FindAll(".nx-filtermenu-foot button").First().Click();

        cut.FindAll(".nx-data-card").Should().HaveCount(2);
        cut.Find(".nx-data-card-title strong").TextContent.Trim().Should().Be("WO-2", "카드 정렬은 보기 전환용 상태로 유지된다");
        cut.Find(".nx-data-card-open").Click();
        selected.Should().BeSameAs(rows[1]);
        cut.FindAll("button").First(button => button.TextContent.Contains("삭제")).HasAttribute("disabled").Should().BeFalse();

        cut.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("분할 상세")).Click();
        cut.FindAll(".rz-data-row").Should().HaveCount(2, "활성 필터가 보기 전환 뒤에도 유지된다");
        cut.Find(".nx-split-detail").GetAttribute("aria-label").Should().Be("빠른 참조 상세");
        cut.Find(".nx-split-detail").TextContent.Should().Contain("WO-2").And.Contain("Busan A").And.NotContain("S2");

        cut.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("카드")).Click();
        cut.FindAll(".nx-data-card.is-selected").Should().ContainSingle("단일 선택도 보기 전환 뒤 유지된다");
        cut.FindAll("button").First(button => button.TextContent.Contains("삭제")).Click();
        deleted.Should().ContainSingle().Which.Should().BeSameAs(rows[1]);

        deleted = null;
        cut.FindAll(".meta-grid-toolbar button").First(button => button.TextContent.Contains("checklist")).Click();
        var selectButton = cut.Find(".nx-data-card-open");
        selectButton.GetAttribute("aria-label").Should().EndWith("선택");
        selectButton.Click();
        cut.Find(".nx-data-card-open").GetAttribute("aria-label").Should().EndWith("선택 해제");
        cut.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("밀집 표")).Click();
        cut.Find(".nx-bulkbar-count").TextContent.Trim().Should().StartWith("1", "다중 선택도 보기 전환 뒤 유지된다");
        cut.FindAll("button").First(button => button.TextContent.Contains("삭제")).Click();
        deleted.Should().ContainSingle().Which.Should().BeSameAs(rows[1]);
    }

    [Fact]
    public void Card_view_pages_client_rows_but_never_slices_a_server_page_twice()
    {
        using var ctx = RadzenContext();
        var columns = new GridColumnDefinition[] { new("ORDER_ID", "작업지시") };
        var clientRows = Enumerable.Range(1, 25)
            .Select(index => new Dictionary<string, object?> { ["ORDER_ID"] = $"WO-{index:D2}" })
            .ToList();
        var client = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, clientRows)
            .Add(component => component.EnableViewModes, true));

        client.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("카드")).Click();
        client.FindAll(".nx-data-card").Should().HaveCount(MetaGridRenderer.PageSize);
        client.FindAll(".nx-card-pager button").Last().Click();
        client.FindAll(".nx-data-card").Should().HaveCount(5);
        client.Find(".nx-card-pager").TextContent.Should().Contain("2 / 2");

        var serverRows = new List<Dictionary<string, object?>>
        {
            new() { ["ORDER_ID"] = "WO-21" },
            new() { ["ORDER_ID"] = "WO-22" },
        };
        var server = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, serverRows)
            .Add(component => component.ServerTotal, 45)
            .Add(component => component.ServerPage, 1)
            .Add(component => component.EnableViewModes, true));
        server.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("카드")).Click();

        server.FindAll(".nx-data-card").Should().HaveCount(2, "서버가 내려준 현재 페이지 행을 다시 20개 단위로 자르면 안 된다");
        server.Find(".meta-grid-pager").TextContent.Should().Contain("2 / 3").And.Contain("45 행");
    }

    [Fact]
    public void Card_view_marks_a_single_visible_page_item_for_the_wide_summary_layout()
    {
        using var ctx = RadzenContext();
        var columns = new GridColumnDefinition[]
        {
            new("ORDER_ID", "수주 번호"),
            new("CUSTOMER", "고객"),
            new("PRODUCT", "품목"),
            new("DUE_DATE", "납기일"),
            new("STATUS", "상태"),
            new("PLAN_QTY", "계획 수량"),
        };
        var cut = ctx.Render<MetaGridRenderer>(parameters => parameters
            .Add(component => component.Columns, columns)
            .Add(component => component.Rows, new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ORDER_ID"] = "SO-001", ["CUSTOMER"] = "고객 A", ["PRODUCT"] = "품목 A",
                    ["DUE_DATE"] = "2026-07-31", ["STATUS"] = "CONFIRMED", ["PLAN_QTY"] = 100,
                },
            })
            .Add(component => component.EnableViewModes, true));

        cut.FindAll(".nx-view-segments button").Single(button => button.TextContent.Contains("카드")).Click();

        cut.Find(".nx-card-view").ClassList.Should().Contain("is-single",
            "현재 카드 페이지가 한 건이면 최대 46rem 요약 레이아웃을 사용해야 한다");
        cut.FindAll(".nx-data-card-fields > div").Should().HaveCount(5);
    }

    [Fact]
    public void Single_card_layout_responds_to_component_width_as_well_as_viewport_width()
    {
        var css = ReadRepoFile("src/01.Web/NexaOne.Web.Components/Components/Meta/MetaGridRenderer.razor.css");

        css.Should().Contain("width: min(100%, 46rem)", "넓은 영역에서도 단일 카드가 과도하게 늘어나면 안 된다");
        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)@container\s*\(max-width:\s*56\.25rem\).*?\.nx-card-view\.is-single\s+\.nx-data-card-fields\s*\{\s*grid-template-columns:\s*repeat\(2")
            .Should().BeTrue("좁은 레이아웃 위젯은 브라우저 폭과 무관하게 2열로 줄어야 한다");
        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)@media\s*\(max-width:\s*480px\).*?\.nx-card-view\.is-single\s+\.nx-data-card-fields\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\)")
            .Should().BeTrue("모바일 단일 카드는 1열로 읽혀야 한다");
    }

    [Fact]
    public void Column_filter_menu_keeps_a_usable_desktop_width_and_becomes_a_drawer_safe_overlay()
    {
        var css = ReadRepoFile("src/01.Web/NexaOne.Web.Components/Components/Meta/MetaGridRenderer.razor.css");

        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)\.nx-filtermenu\s*\{[^}]*box-sizing:\s*border-box;[^}]*width:\s*30rem;[^}]*min-width:\s*22rem;")
            .Should().BeTrue("데스크톱 필터는 28px 트리거 래퍼가 아니라 자체 읽기 폭을 가져야 한다");
        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)@media\s*\(max-width:\s*1100px\).*?\.nx-filtermenu\s*\{[^}]*position:\s*fixed;[^}]*right:\s*1rem;[^}]*left:\s*1rem;[^}]*width:\s*auto;[^}]*min-width:\s*0;")
            .Should().BeTrue("사이드바가 드로어로 전환되는 폭에서는 필터가 뷰포트 안의 고정 오버레이여야 한다");
        css.Should().NotContain("width: min(30rem, calc(100% -",
            "팝업의 100%는 28px 트리거 래퍼 기준이라 필터가 읽을 수 없게 축소된다");
    }

    [Fact]
    public void Grid_css_keeps_intrinsic_table_width_inside_the_data_scroll_viewport()
    {
        var css = ReadRepoFile("src/01.Web/NexaOne.Web.Components/Components/Meta/MetaGridRenderer.razor.css");

        // Radzen의 루트 div에도 rz-datatable 클래스가 있다. 해당 루트에 max-content를 적용하면
        // 그리드가 콘텐츠 폭으로 커져 문서 전체가 가로 스크롤되는 회귀가 생긴다.
        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)\.meta-grid\s+::deep\s+\.rz-datatable\s*(?:,|\{)")
            .Should().BeFalse("intrinsic 폭은 Radzen 루트가 아니라 실제 table에만 적용해야 한다");

        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)\.meta-grid\s+::deep\s+table\.rz-grid-table\s*\{[^}]*min-width:\s*max-content")
            .Should().BeTrue("넓은 열은 실제 table의 intrinsic 폭을 유지해야 한다");

        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)\.meta-grid\s+::deep\s+\.rz-data-grid\s*\{[^}]*min-width:\s*0;[^}]*overflow:\s*hidden")
            .Should().BeTrue("Radzen 루트는 부모 폭 안에서 줄어들고 표의 넘침을 외부로 누출하지 않아야 한다");

        System.Text.RegularExpressions.Regex.IsMatch(
                css,
                @"(?s)\.meta-grid\s+::deep\s+\.rz-data-grid-data\s*\{[^}]*min-width:\s*0;[^}]*overflow:\s*auto")
            .Should().BeTrue("가로·세로 스크롤의 단일 경계는 data viewport여야 한다");
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(RepositorySource.GetFile(relativePath));
}
