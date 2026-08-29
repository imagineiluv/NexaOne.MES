using System.Text.Json;
using System.Text.Json.Serialization;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>TS↔C# 화면정의 계약 드리프트 가드 — 공유 픽스처(test/contract/screen-definition-contract.json)와
/// C# record 속성을 리플렉션으로 대조한다. SPA 쪽은 vitest(contract-drift.test.ts)가 같은 픽스처와
/// layout.ts SCREEN_DEFINITION_KEYS(타입 완전성 체크 포함)를 대조 — 어느 한쪽만 속성을 추가하면
/// 반대편 스위트가 실패한다(DeleteQueryId SPA 미러 누락 실사고 재발 방지).</summary>
public sealed class ScreenContractDriftTests
{
    private static JsonDocument LoadFixture()
    {
        var path = RepositorySource.GetFile(
            "test", "contract", "screen-definition-contract.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string[] FixtureNames(string key)
    {
        using var doc = LoadFixture();
        return doc.RootElement.GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static string[] RecordPropertyNames(Type t)
        => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .ToArray();

    private static string[] DeclaredPropertyNames(Type type)
        => type.GetProperties(System.Reflection.BindingFlags.Public
                              | System.Reflection.BindingFlags.Instance
                              | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .ToArray();

    [Fact]
    public void ScreenDefinition_properties_match_shared_contract_fixture()
    {
        var actual = RecordPropertyNames(typeof(ScreenDefinition));
        var expected = FixtureNames("screenDefinition");
        actual.Should().BeEquivalentTo(expected,
            "C# ScreenDefinition에 속성을 추가/삭제하면 공유 픽스처와 SPA 미러(layout.ts DTO+KEYS, mapping, api extras, ScreenEditor)를 함께 갱신해야 한다 — 픽스처의 _comment 절차 참조");
    }

    [Fact]
    public void BulkCommandDefinition_properties_match_shared_contract_fixture()
    {
        var actual = RecordPropertyNames(typeof(BulkCommandDefinition));
        var expected = FixtureNames("bulkCommandDefinition");
        actual.Should().BeEquivalentTo(expected,
            "BulkCommandDefinition 변경 시 SPA layout.ts BulkCommandDefinition 인터페이스와 픽스처를 함께 갱신해야 한다");
    }

    [Fact]
    public void FieldDefinition_properties_match_shared_contract_fixture()
    {
        var actual = RecordPropertyNames(typeof(FieldDefinition));
        var expected = FixtureNames("fieldDefinition");
        actual.Should().BeEquivalentTo(expected,
            "숨김·자동 생성 등 FieldDefinition 변경은 런타임과 Designer trait/mapping에 동시에 반영해야 한다");
    }

    [Fact]
    public void FieldValueGenerator_values_match_shared_contract_fixture()
        => Enum.GetNames<FieldValueGenerator>().Should().Equal(FixtureNames("fieldValueGenerator"));

    [Fact]
    public void Scoped_widget_properties_match_shared_contract_fixture()
    {
        DeclaredPropertyNames(typeof(GridWidget)).Should().BeEquivalentTo(FixtureNames("gridWidget"));
        DeclaredPropertyNames(typeof(FormWidget)).Should().BeEquivalentTo(FixtureNames("formWidget"));
        DeclaredPropertyNames(typeof(CollectionWidget)).Should().BeEquivalentTo(FixtureNames("collectionWidget"));
        DeclaredPropertyNames(typeof(ButtonWidget)).Should().BeEquivalentTo(FixtureNames("buttonWidget"));
    }

    [Fact]
    public void Layout_discriminators_match_shared_contract_fixture()
    {
        var actual = typeof(LayoutNode)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(attribute => attribute.TypeDiscriminator?.ToString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        actual.Should().Equal(FixtureNames("layoutKinds"),
            "새 위젯 discriminator는 C# 다형 JSON, Portal 타입·매핑·블록에서 같은 순서로 관리해야 한다");
    }

    [Fact]
    public void ScreenPurpose_values_match_shared_contract_fixture()
    {
        var actual = Enum.GetNames<ScreenPurpose>();
        var expected = FixtureNames("screenPurpose");
        actual.Should().Equal(expected,
            "화면 목적 값과 순서는 C# enum, 공유 픽스처, SPA SCREEN_PURPOSE_VALUES에서 같아야 한다");
    }
}
