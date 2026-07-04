using Bunit;
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
        => MetaGridRenderer.SeverityOf(value).Should().Be(expected);

    [Theory]
    [InlineData("PLANT01")]
    [InlineData("부산공장")]
    [InlineData("")]
    [InlineData("2026-07-04")]
    public void Unknown_values_render_plain_no_badge(string value)
        => MetaGridRenderer.SeverityOf(value).Should().BeNull("알려지지 않은 값은 배지로 오탐하지 않아야 한다");

    [Fact]
    public void Iso_timestamp_is_humanized()
    {
        // 원시 ISO(소수점·Z)를 "yyyy-MM-dd HH:mm:ss" 로컬로 정형화한다(P1 — 원시 타임스탬프 제거).
        var formatted = MetaGridRenderer.FormatCell("2026-07-04T12:30:44.0049805Z");
        formatted.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
        formatted.Should().NotContain("T").And.NotContain("Z");
    }

    [Fact]
    public void Non_timestamp_value_is_returned_verbatim()
        => MetaGridRenderer.FormatCell("EQ01/TEMP 78.5").Should().Be("EQ01/TEMP 78.5");

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
}
