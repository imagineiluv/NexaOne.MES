using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>재귀 레이아웃 렌더러 — 컨테이너 구조를 그리고, 그리드는 위젯별 결과맵의 행을,
/// 폼/필드는 공유 Model을, 명령 버튼은 콜백을 연결하는지 검증한다.</summary>
public sealed class LayoutRendererTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, object?>>> NoResults
        = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>();

    private static IRenderedComponent<LayoutRenderer> Render(
        TestContext ctx, LayoutNode layout, Dictionary<string, object?>? model = null,
        IReadOnlyDictionary<string, IReadOnlyList<Dictionary<string, object?>>>? results = null,
        Action<string>? onCommand = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();   // 트렌드 차트는 RadzenChart — 렌더에 Radzen 서비스 필요
        return ctx.RenderComponent<LayoutRenderer>(p => p
            .Add(c => c.Node, layout)
            .Add(c => c.Model, model ?? new Dictionary<string, object?>())
            .Add(c => c.QueryResults, results ?? NoResults)
            .Add(c => c.OnCommand, onCommand is null
                ? default
                : Microsoft.AspNetCore.Components.EventCallback.Factory.Create(new object(), onCommand)));
    }

    [Fact]
    public void Renders_section_row_column_structure_with_text()
    {
        using var ctx = new TestContext();
        var layout = new SectionNode
        {
            Title = "마스터",
            Children = new LayoutNode[]
            {
                new RowNode { Children = new LayoutNode[]
                {
                    new ColumnNode { Span = 6, Children = new LayoutNode[] { new TextWidget { Text = "왼쪽" } } },
                    new ColumnNode { Span = 6, Children = new LayoutNode[] { new TextWidget { Text = "오른쪽", IsLabel = true } } },
                } },
            },
        };

        var cut = Render(ctx, layout);

        cut.Markup.Should().Contain("마스터").And.Contain("왼쪽").And.Contain("오른쪽");
        cut.FindAll(".layout-column").Count.Should().Be(2, "Row 아래 Column 2개가 렌더돼야 한다");
    }

    [Fact]
    public void Kpi_widget_renders_value_from_query_result_and_dash_when_missing()
    {
        using var ctx = new TestContext();
        var layout = new RowNode
        {
            Children = new LayoutNode[]
            {
                new KpiWidget { Id = "k1", Label = "활성 알람", QueryId = "SYS.DashboardSummary", ValueColumn = "ACTIVE_ALARMS", Unit = "건" },
                new KpiWidget { Id = "k2", Label = "미바인딩", QueryId = "NO.SuchQuery", ValueColumn = "X" },
            },
        };
        var results = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>
        {
            ["SYS.DashboardSummary"] = new List<Dictionary<string, object?>>
            {
                new() { ["ACTIVE_ALARMS"] = 7L },
            },
        };

        var cut = Render(ctx, layout, results: results);

        cut.Markup.Should().Contain("활성 알람").And.Contain("7").And.Contain("건",
            "KPI는 바인딩 쿼리 첫 행의 ValueColumn 값 + 단위를 표시해야 한다");
        cut.Markup.Should().Contain("—", "미실행/미바인딩 KPI는 대시(—)로 빈 카드를 방지해야 한다");
        cut.FindAll(".layout-kpi").Count.Should().Be(2);
    }

    [Fact]
    public void Badge_widget_matches_style_rules_and_falls_back_to_neutral()
    {
        using var ctx = new TestContext();
        var styles = new BadgeStyleRule[]
        {
            new("RUN", "success", "가동"),
            new("DOWN", "danger"),
            new("IDLE", "not-a-severity"),   // 화이트리스트 밖 심각도 → neutral 강제(CSS 클래스 주입 차단)
        };
        var layout = new RowNode
        {
            Children = new LayoutNode[]
            {
                new BadgeWidget { Id = "b1", Label = "설비 상태", QueryId = "Q.Run", ValueColumn = "STATE", Styles = styles },
                new BadgeWidget { Id = "b2", QueryId = "Q.Down", ValueColumn = "STATE", Styles = styles },
                new BadgeWidget { Id = "b3", QueryId = "Q.Idle", ValueColumn = "STATE", Styles = styles },
                new BadgeWidget { Id = "b4", QueryId = "Q.Unknown", ValueColumn = "STATE", Styles = styles },
                new BadgeWidget { Id = "b5", QueryId = "NO.Data", ValueColumn = "STATE", Styles = styles },
            },
        };
        var results = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>
        {
            ["Q.Run"] = new List<Dictionary<string, object?>> { new() { ["STATE"] = "run" } },      // 대소문자 무시 매칭
            ["Q.Down"] = new List<Dictionary<string, object?>> { new() { ["STATE"] = "DOWN" } },
            ["Q.Idle"] = new List<Dictionary<string, object?>> { new() { ["STATE"] = "IDLE" } },
            ["Q.Unknown"] = new List<Dictionary<string, object?>> { new() { ["STATE"] = "PM" } },   // 규칙 미등록 상태
        };

        var cut = Render(ctx, layout, results: results);

        // 배지는 RadzenBadge — 텍스트 매핑(DisplayText/원문/대시)은 정적 마크업에서 확인, 심각도→스타일
        // 매핑은 BadgeStyleOf(순수)로 검증(enum 매핑이라 화이트리스트 밖 심각도의 CSS 클래스 주입이 원천 불가).
        cut.FindAll(".rz-badge").Count.Should().Be(5, "각 위젯이 RadzenBadge로 렌더돼야 한다");
        cut.Markup.Should().Contain("가동", "매칭 규칙의 DisplayText가 적용돼야 한다");
        cut.Markup.Should().Contain("DOWN", "DisplayText 없으면 원문 표시");
        cut.Markup.Should().Contain("PM", "규칙 미등록 값은 원문 표시(화면 불파손)");
        cut.Markup.Should().Contain("—", "데이터 부재는 대시(—)");
    }

    [Theory]
    [InlineData("success", Radzen.BadgeStyle.Success)]
    [InlineData("danger", Radzen.BadgeStyle.Danger)]
    [InlineData("warning", Radzen.BadgeStyle.Warning)]
    [InlineData("info", Radzen.BadgeStyle.Info)]
    [InlineData("neutral", Radzen.BadgeStyle.Light)]
    [InlineData("not-a-severity", Radzen.BadgeStyle.Light)]
    public void BadgeStyleOf_maps_known_severities_and_falls_back_to_light(string severity, Radzen.BadgeStyle expected)
        => LayoutRenderer.BadgeStyleOf(severity).Should().Be(expected,
            "알려진 심각도만 색 스타일, 그 외(neutral·미등록)는 Light — enum 매핑이라 임의 클래스 주입 불가");

    // 트렌드 차트는 RadzenChart(라이브러리) — 실제 SVG/스케일/범례/툴팁은 라이브러리가 JS로 그리고
    // bUnit 정적 렌더에선 JS 측정 부재로 렌더가 불안정하다(브라우저 스모크로 검증). 여기서는 우리가
    // 소유한 것만: (a) 2점 미만이면 안내 문구, (b) 시리즈 포인트→차트 데이터 매핑(ChartData).

    [Fact]
    public void Trend_chart_with_insufficient_data_shows_notice_not_chart()
    {
        using var ctx = new TestContext();
        var layout = new TrendChartWidget { Id = "tc2", Label = "빈 트렌드", QueryId = "NO.Data", ValueColumn = "VALUE" };
        var cut = Render(ctx, layout);
        cut.Markup.Should().Contain("데이터가 부족").And.NotContain("rz-chart");
    }

    [Fact]
    public void ChartData_maps_time_to_category_or_falls_back_to_index()
    {
        // 시각이 있으면 축 라벨=시각, 없으면 1부터의 순번. 값은 double로 변환.
        var withTime = LayoutRenderer.ChartData(new() { (20m, "10:00:00"), (80m, "10:00:01") });
        withTime.Should().HaveCount(2);
        withTime[0].Should().Be(new LayoutRenderer.ChartPoint("10:00:00", 20d));
        withTime[1].Should().Be(new LayoutRenderer.ChartPoint("10:00:01", 80d));

        var noTime = LayoutRenderer.ChartData(new() { (5m, null), (8m, null), (3m, null) });
        noTime.Select(p => p.Category).Should().Equal("1", "2", "3");
        noTime.Select(p => p.Value).Should().Equal(5d, 8d, 3d);
    }

    [Fact]
    public void Grid_widget_renders_rows_from_query_result_map()
    {
        using var ctx = new TestContext();
        var layout = new GridWidget
        {
            QueryId = "MDM.PlantList",
            Columns = new GridColumnDefinition[] { new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명") },
        };
        var results = new Dictionary<string, IReadOnlyList<Dictionary<string, object?>>>
        {
            ["MDM.PlantList"] = new List<Dictionary<string, object?>>
            {
                new() { ["PLANT_ID"] = "P-1", ["PLANT_NAME"] = "Plant One" },
            },
        };

        var cut = Render(ctx, layout, results: results);

        cut.Markup.Should().Contain("공장 ID").And.Contain("Plant One");
        cut.FindAll("tbody tr").Count.Should().Be(1);
    }

    [Fact]
    public void Command_button_invokes_OnCommand_with_command_id()
    {
        using var ctx = new TestContext();
        string? invoked = null;
        var layout = new ButtonWidget { Label = "승인", Command = "SYS.Approve" };

        var cut = Render(ctx, layout, onCommand: c => invoked = c);
        cut.Find("button").Click();

        invoked.Should().Be("SYS.Approve", "명령 버튼은 OnCommand에 command id를 전달해야 한다");
    }

    [Fact]
    public void Field_widget_two_way_binds_shared_model()
    {
        using var ctx = new TestContext();
        var model = new Dictionary<string, object?>();
        var layout = new FormWidget
        {
            SaveQueryId = "MDM.CreatePlant",
            Fields = new FieldWidget[]
            {
                new() { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text) },
            },
        };

        var cut = Render(ctx, layout, model: model);
        cut.Find("input").Change("플랜트1");

        model.Should().ContainKey("plantName");
        model["plantName"]!.ToString().Should().Be("플랜트1");
    }
}
