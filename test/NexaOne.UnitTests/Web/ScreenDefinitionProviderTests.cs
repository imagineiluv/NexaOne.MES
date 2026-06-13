using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>Phase 3 — 메타데이터 화면 정의 제공자(시드/해석/대소문자/등록)를 검증. .razor 렌더는 빌드 검증 영역.</summary>
public sealed class ScreenDefinitionProviderTests
{
    [Fact]
    public void Seeded_demo_screen_resolves_with_required_fields()
    {
        var provider = new InMemoryScreenDefinitionProvider();

        var def = provider.Get("DEMO_PARAM");

        def.Should().NotBeNull();
        def!.Fields.Should().NotBeEmpty();
        def.Fields.Should().Contain(f => f.Key == "parameterId" && f.Required);
        def.Fields.Should().Contain(f => f.Key == "isActive" && f.Type == FieldType.Boolean);
    }

    [Fact]
    public void Get_is_case_insensitive()
        => new InMemoryScreenDefinitionProvider().Get("demo_param").Should().NotBeNull();

    [Fact]
    public void Unknown_uiId_returns_null_and_tryget_false()
    {
        var provider = new InMemoryScreenDefinitionProvider();

        provider.Get("NOPE").Should().BeNull();
        provider.TryGet("NOPE", out var d).Should().BeFalse();
        d.Should().BeNull();
    }

    [Fact]
    public void Register_adds_and_overwrites_definition()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        provider.Register(new ScreenDefinition("X1", "Custom", new FieldDefinition[] { new("a", "A") }));

        provider.Get("X1")!.Title.Should().Be("Custom");
    }
}
