using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>Phase 4 후속 — ScreenDefinition JSON 직렬화 라운드트립(DB 저장소·디자이너 내보내기의 기반).</summary>
public sealed class ScreenDefinitionJsonTests
{
    [Fact]
    public void Serialize_deserialize_roundtrips_fields_and_columns()
    {
        var def = new ScreenDefinition("S1", "타이틀",
            new FieldDefinition[]
            {
                new("p", "P", FieldType.Number, Required: true),
                new("sel", "선택", FieldType.Select, Options: new[] { "A", "B" }),
            },
            new GridColumnDefinition[] { new("c1", "컬럼1", Visible: false) });

        var json = ScreenDefinitionJson.Serialize(def);
        var back = ScreenDefinitionJson.Deserialize(json);

        back.Should().NotBeNull();
        back!.UiId.Should().Be("S1");
        back.Title.Should().Be("타이틀");
        back.Fields.Should().HaveCount(2);
        back.Fields[0].Type.Should().Be(FieldType.Number, "enum은 문자열로 직렬화·복원된다");
        back.Fields[0].Required.Should().BeTrue();
        back.Fields[1].Type.Should().Be(FieldType.Select);
        back.Fields[1].Options.Should().BeEquivalentTo(new[] { "A", "B" });
        back.Columns.Should().ContainSingle();
        back.Columns![0].Visible.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_invalid_json_returns_null()
        => ScreenDefinitionJson.Deserialize("{ not valid").Should().BeNull();
}
