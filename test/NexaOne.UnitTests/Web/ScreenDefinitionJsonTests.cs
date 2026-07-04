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
            new GridColumnDefinition[] { new("c1", "컬럼1", Visible: false) },
            QueryId: "MDM.PlantList");

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
        back.QueryId.Should().Be("MDM.PlantList", "그리드 데이터 소스 쿼리 ID도 라운드트립돼야 한다");
    }

    [Fact]
    public void Deserialize_invalid_json_returns_null()
        => ScreenDefinitionJson.Deserialize("{ not valid").Should().BeNull();

    [Fact]
    public void CountQueryId_roundtrips_and_defaults_to_null()
    {
        // 서버측 페이징(P3-9 v2) — 화면 수준 설정이 저장/재로드에서 드랍되지 않아야 한다(SPA 미러와 동일 계약).
        var def = new ScreenDefinition("S2", "로그", Array.Empty<FieldDefinition>(),
            QueryId: "SYS.AppLogList", CountQueryId: "SYS.AppLogListCount");
        var back = ScreenDefinitionJson.Deserialize(ScreenDefinitionJson.Serialize(def));
        back!.CountQueryId.Should().Be("SYS.AppLogListCount");

        // 기존 정의(속성 부재)는 null 정규화 — 하위호환.
        ScreenDefinitionJson.Deserialize("""{"uiId":"OLD","title":"t","fields":[]}""")!
            .CountQueryId.Should().BeNull();
    }
}
