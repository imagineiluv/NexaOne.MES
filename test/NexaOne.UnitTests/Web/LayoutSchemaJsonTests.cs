using System.Text.Json;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>레이아웃 트리 직렬화 라운드트립 + 분리 파싱/폴백.
/// 핵심 불변식: layout이 깨져도(미지 kind·과대 깊이) 화면 전체가 null이 되지 않고 평면 경로로 폴백한다.</summary>
public sealed class LayoutSchemaJsonTests
{
    private static ScreenDefinition Sample() => new(
        "PLANT_MGMT", "공장 관리",
        new FieldDefinition[] { new("plantId", "공장 ID", FieldType.Text, Required: true) },
        Layout: new SectionNode
        {
            Id = "sec-root", Title = "공장 마스터",
            Children = new LayoutNode[]
            {
                new RowNode { Id = "row-1", Children = new LayoutNode[]
                {
                    new ColumnNode { Span = 7, Children = new LayoutNode[]
                    {
                        new GridWidget { Id = "grid-plants", QueryId = "MDM.PlantList",
                            Columns = new GridColumnDefinition[] { new("PLANT_ID", "공장 ID") } },
                    } },
                    new ColumnNode { Span = 5, Children = new LayoutNode[]
                    {
                        new FormWidget { Id = "form-plant", SaveQueryId = "MDM.CreatePlant",
                            Fields = new FieldWidget[] { new() { FieldKey = "plantId",
                                Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) } } },
                        new ButtonWidget { Label = "저장", Command = "MDM.CreatePlant", RequiredPermission = "mdm:manage" },
                    } },
                } },
            },
        });

    [Fact]
    public void Roundtrips_full_layout_tree_losslessly()
    {
        var json = ScreenDefinitionJson.Serialize(Sample());
        var back = ScreenDefinitionJson.Deserialize(json);

        back.Should().NotBeNull();
        back!.Layout.Should().BeOfType<SectionNode>();
        var section = (SectionNode)back.Layout!;
        section.Title.Should().Be("공장 마스터");
        var row = (RowNode)section.Children![0];
        var col0 = (ColumnNode)row.Children![0];
        col0.Span.Should().Be(7);
        var grid = (GridWidget)col0.Children![0];
        grid.QueryId.Should().Be("MDM.PlantList");
        grid.Id.Should().Be("grid-plants", "노드 Id가 라운드트립돼야 한다");
        var col1 = (ColumnNode)row.Children![1];
        var btn = (ButtonWidget)col1.Children![1];
        btn.Command.Should().Be("MDM.CreatePlant");
        btn.RequiredPermission.Should().Be("mdm:manage");
    }

    [Fact]
    public void Null_layout_roundtrips_to_null()
    {
        var def = new ScreenDefinition("S", "T",
            new FieldDefinition[] { new("a", "A") });
        var back = ScreenDefinitionJson.Deserialize(ScreenDefinitionJson.Serialize(def));
        back.Should().NotBeNull();
        back!.Layout.Should().BeNull();
    }

    [Fact]
    public void Deserializes_layout_when_kind_is_not_first_property()
    {
        const string json = """
        {
          "uiId": "S", "title": "T",
          "fields": [],
          "layout": { "title": "섹션", "id": "s1", "kind": "section", "children": [
            { "children": [], "kind": "row" }
          ] }
        }
        """;
        var back = ScreenDefinitionJson.Deserialize(json);
        back.Should().NotBeNull();
        back!.Layout.Should().BeOfType<SectionNode>();
        ((SectionNode)back.Layout!).Title.Should().Be("섹션");
    }

    [Fact]
    public void Unknown_kind_falls_back_to_flat_definition_not_whole_null()
    {
        const string json = """
        {
          "uiId": "S", "title": "T",
          "fields": [ { "key": "a", "label": "A", "type": "Text" } ],
          "layout": { "kind": "carousel", "id": "x" }
        }
        """;
        var back = ScreenDefinitionJson.Deserialize(json);
        back.Should().NotBeNull("미지 kind는 layout만 폴백시키고 평면 정의는 보존돼야 한다");
        back!.UiId.Should().Be("S");
        back.Fields.Should().ContainSingle();
        back.Layout.Should().BeNull("미지 kind layout은 null로 폴백된다");
    }

    [Fact]
    public void Over_max_depth_layout_falls_back_to_flat_definition()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""{ "uiId":"S","title":"T","fields":[],"layout": """);
        const int depth = 80;
        for (var i = 0; i < depth; i++) sb.Append("""{ "kind":"section","children":[""");
        for (var i = 0; i < depth; i++) sb.Append("]}");
        sb.Append(" }");
        var back = ScreenDefinitionJson.Deserialize(sb.ToString());
        back.Should().NotBeNull();
        back!.Layout.Should().BeNull("과대 깊이 layout은 폴백, 평면 정의는 보존");
    }

    [Fact]
    public void Invalid_json_still_returns_null()
        => ScreenDefinitionJson.Deserialize("{ not valid").Should().BeNull();
}
