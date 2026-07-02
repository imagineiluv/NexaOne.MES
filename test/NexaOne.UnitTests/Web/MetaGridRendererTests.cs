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

        cut.Markup.Should().Contain("table-layout:fixed", "폭 지정 컬럼이 있으면 고정 레이아웃이어야 한다");
        cut.FindAll("colgroup col").Count.Should().Be(2, "보이는 컬럼 수만큼 col(숨김 제외)");
        cut.Markup.Should().Contain("width:120px");
        cut.Markup.Should().NotContain("width:50px", "숨김 컬럼 폭은 렌더되지 않아야 한다");
    }

    [Fact]
    public void No_width_meta_keeps_auto_layout_without_colgroup()
    {
        using var ctx = new TestContext();
        var cut = Render(ctx, rows: new List<Dictionary<string, object?>>());

        cut.Markup.Should().NotContain("table-layout:fixed", "폭 미지정 화면은 기존 자동 레이아웃 유지(하위호환)");
        cut.FindAll("colgroup").Should().BeEmpty();
    }
}
