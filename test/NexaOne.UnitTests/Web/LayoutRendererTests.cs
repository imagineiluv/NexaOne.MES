using Bunit;
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
