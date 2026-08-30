using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 반복 항목 등록 화면의 모델 수명주기와 인덱스 검증을 잠급니다. 멱등키는 실패 재시도 중 유지되고
/// 서버 성공이 확인된 뒤에만 다음 등록용 값으로 교체되어야 합니다.
/// </summary>
public sealed class MetaScreenCollectionRegistrationTests
{
    [Fact]
    public void Failed_save_keeps_uuid_and_success_rotates_uuid_and_rebuilds_minimum_items()
    {
        using var ctx = CreateContext();
        var definition = RegistrationDefinition(itemRequired: false);
        var api = new Mock<IApiClient>();
        var calls = new List<(string IdempotencyKey, int ItemCount)>();
        var attempt = 0;
        api.Setup(client => client.ExecuteCommandAsync(
                "QMS.TestRegister",
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>((_, parameters, _) =>
            {
                var model = parameters.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
                var key = model["idempotencyKey"].Should().BeOfType<string>().Subject;
                var items = model["items"].Should().BeAssignableTo<IReadOnlyList<Dictionary<string, object?>>>().Subject;
                calls.Add((key, items.Count));
            })
            .ReturnsAsync(() => ++attempt >= 2);
        Register(ctx, definition, api);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.FindComponents<MetaCollectionEditor>().Should().ContainSingle());
        var initialModel = SharedModel(cut);
        var initialKey = initialModel["idempotencyKey"].Should().BeOfType<string>().Subject;
        Guid.TryParse(initialKey, out _).Should().BeTrue();
        cut.Markup.Should().NotContain("멱등 키", "숨김 시스템 필드는 사용자 폼에 노출하지 않는다");
        ((IReadOnlyList<Dictionary<string, object?>>)initialModel["items"]!).Should().ContainSingle();

        cut.Find("button.layout-command").Click();
        cut.WaitForAssertion(() => calls.Should().ContainSingle());
        SharedModel(cut)["idempotencyKey"].Should().Be(initialKey,
            "실패 재시도 전에 멱등키를 바꾸면 동일 요청을 안전하게 재전송할 수 없다");

        cut.Find("button.layout-command").Click();
        cut.WaitForAssertion(() => calls.Should().HaveCount(2));

        calls.Select(call => call.IdempotencyKey).Should().OnlyContain(key => key == initialKey);
        calls.Select(call => call.ItemCount).Should().OnlyContain(count => count == 1);
        var nextModel = SharedModel(cut);
        nextModel["idempotencyKey"].Should().BeOfType<string>().Which.Should().NotBe(initialKey);
        ((IReadOnlyList<Dictionary<string, object?>>)nextModel["items"]!).Should().ContainSingle(
            "성공 뒤 새 등록 모델도 collection MinItems를 즉시 만족해야 한다");
    }

    [Fact]
    public void Required_collection_field_uses_indexed_error_and_blocks_command()
    {
        using var ctx = CreateContext();
        var definition = RegistrationDefinition(itemRequired: true);
        var api = new Mock<IApiClient>();
        Register(ctx, definition, api);

        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.FindAll("fieldset.meta-collection-item").Should().ContainSingle());
        cut.Find("button.layout-command").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".meta-field-error").TextContent.Should().Contain("검사 항목 1").And.Contain("필수"));
        api.Verify(client => client.ExecuteCommandAsync(
            It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ScreenDefinition RegistrationDefinition(bool itemRequired)
        => new(
            "COLLECTION_REGISTER",
            "반복 항목 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new FormWidget
                    {
                        SaveQueryId = "QMS.TestRegister",
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
                    new CollectionWidget
                    {
                        CollectionKey = "items",
                        Label = "검사 항목",
                        ItemLabel = "검사 항목",
                        MinItems = 1,
                        Fields =
                        [
                            new FieldWidget
                            {
                                FieldKey = "specId",
                                Field = new FieldDefinition("specId", "검사 규격", Required: itemRequired),
                            },
                        ],
                    },
                    new ButtonWidget { Label = "등록", Command = "QMS.TestRegister" },
                ],
            },
            Purpose: ScreenPurpose.Register);

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddRadzenComponents();
        return context;
    }

    private static void Register(
        BunitContext context,
        ScreenDefinition definition,
        Mock<IApiClient> api)
    {
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.GetAsync(definition.UiId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        context.Services.AddSingleton(provider.Object);
        context.Services.AddSingleton(api.Object);
    }

    private static Dictionary<string, object?> SharedModel(IRenderedComponent<MetaScreen> cut)
        => cut.FindComponents<LayoutRenderer>().First().Instance.Model;
}
