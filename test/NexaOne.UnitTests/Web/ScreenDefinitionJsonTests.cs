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

    [Fact]
    public void Purpose_roundtrips_and_legacy_definition_defaults_to_auto()
    {
        var definition = new ScreenDefinition(
            "REGISTER1",
            "등록 화면",
            Array.Empty<FieldDefinition>(),
            Purpose: ScreenPurpose.Register);

        var json = ScreenDefinitionJson.Serialize(definition);
        var back = ScreenDefinitionJson.Deserialize(json);

        back.Should().NotBeNull();
        back!.Purpose.Should().Be(ScreenPurpose.Register);
        json.Should().Contain("\"purpose\": \"Register\"", "화면 목적 enum은 Designer와 공유하는 문자열 계약이다");

        ScreenDefinitionJson.Deserialize("""{"uiId":"OLD","title":"기존","fields":[]}""")!
            .Purpose.Should().Be(ScreenPurpose.Auto, "기존 JSON은 현재 렌더링 동작을 그대로 유지해야 한다");
    }

    [Fact]
    public void Flat_and_bulk_permissions_roundtrip_without_breaking_legacy_json()
    {
        var definition = new ScreenDefinition(
            "SECURED",
            "권한 화면",
            Array.Empty<FieldDefinition>(),
            BulkCommands:
            [
                new BulkCommandDefinition(
                    "승인",
                    "QMS.Approve",
                    RequiredPermission: "qms:manage"),
            ],
            ReadRequiredPermission: "qms:read",
            SaveRequiredPermission: "qms:manage",
            DeleteRequiredPermission: "qms:manage");

        var json = ScreenDefinitionJson.Serialize(definition);
        var back = ScreenDefinitionJson.Deserialize(json);

        back.Should().NotBeNull();
        back!.ReadRequiredPermission.Should().Be("qms:read");
        back.SaveRequiredPermission.Should().Be("qms:manage");
        back.DeleteRequiredPermission.Should().Be("qms:manage");
        back.BulkCommands.Should().ContainSingle()
            .Which.RequiredPermission.Should().Be("qms:manage");

        var legacy = ScreenDefinitionJson.Deserialize(
            """{"uiId":"OLD","title":"기존","fields":[],"bulkCommands":[{"label":"승인","commandQueryId":"QMS.Approve"}]}""");
        legacy.Should().NotBeNull();
        legacy!.ReadRequiredPermission.Should().BeNull();
        legacy.SaveRequiredPermission.Should().BeNull();
        legacy.DeleteRequiredPermission.Should().BeNull();
        legacy.BulkCommands.Should().ContainSingle().Which.RequiredPermission.Should().BeNull();
    }

    [Fact]
    public void Collection_hidden_field_and_value_generator_roundtrip_with_legacy_defaults()
    {
        var collection = new CollectionWidget
        {
            Id = "inspection-items",
            CollectionKey = "items",
            Label = "검사 항목",
            ItemLabel = "항목",
            BindingScope = "inspection",
            MinItems = 1,
            MaxItems = 20,
            Fields =
            [
                new FieldWidget
                {
                    Id = "item-spec",
                    FieldKey = "specId",
                    Field = new FieldDefinition("specId", "검사 규격", FieldType.Select, Required: true),
                },
            ],
        };
        var definition = new ScreenDefinition(
            "QMS_REGISTER",
            "검사 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new FormWidget
                    {
                        Fields =
                        [
                            new FieldWidget
                            {
                                FieldKey = "idempotencyKey",
                                Field = new FieldDefinition(
                                    "idempotencyKey",
                                    "멱등 키",
                                    Required: true,
                                    Hidden: true,
                                    ValueGenerator: FieldValueGenerator.UuidV4),
                            },
                        ],
                    },
                    collection,
                ],
            });

        var json = ScreenDefinitionJson.Serialize(definition);
        var back = ScreenDefinitionJson.Deserialize(json);

        var section = back!.Layout.Should().BeOfType<SectionNode>().Subject;
        section.Children.Should().NotBeNull();
        var children = section.Children!;
        var form = children.OfType<FormWidget>().Single();
        form.Fields.Should().NotBeNull();
        var restoredHeaderField = form.Fields!.Single().Field!;
        restoredHeaderField.Hidden.Should().BeTrue();
        restoredHeaderField.ValueGenerator.Should().Be(FieldValueGenerator.UuidV4);
        var restoredCollection = children.OfType<CollectionWidget>().Single();
        restoredCollection.CollectionKey.Should().Be("items");
        restoredCollection.BindingScope.Should().Be("inspection");
        restoredCollection.MinItems.Should().Be(1);
        restoredCollection.MaxItems.Should().Be(20);
        restoredCollection.Fields.Should().ContainSingle();
        json.Should().Contain("\"kind\": \"collection\"");

        var legacy = ScreenDefinitionJson.Deserialize(
            """{"uiId":"OLD","title":"기존","fields":[{"key":"name","label":"이름","type":"Text"}]}""");
        legacy!.Fields.Single().Hidden.Should().BeFalse();
        legacy.Fields.Single().ValueGenerator.Should().Be(FieldValueGenerator.None);
    }
}
