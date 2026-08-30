using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 반복 입력 편집기가 화면별 모델에 결합되지 않고 필드 스키마, 항목 경계, 권한 차단과 값 격리를 지키는지 검증합니다.
/// </summary>
public sealed class MetaCollectionEditorTests
{
    private static readonly IReadOnlyList<FieldDefinition> Schema =
    [
        new("name", "이름", Required: true),
        new("qty", "수량", FieldType.Number),
    ];

    [Fact]
    public void Minimum_is_normalized_maximum_blocks_add_and_each_item_reuses_meta_form()
    {
        using var ctx = RadzenContext();
        var published = new List<List<Dictionary<string, object?>>>();
        var cut = ctx.Render<MetaCollectionEditor>(parameters => parameters
            .Add(component => component.Schema, Schema)
            .Add(component => component.Items, new List<Dictionary<string, object?>>())
            .Add(component => component.MinItems, 1)
            .Add(component => component.MaxItems, 2)
            .Add(component => component.ItemFactory,
                () => new Dictionary<string, object?> { ["qty"] = "0" })
            .Add(component => component.ItemsChanged,
                items => published.Add(Clone(items))));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("fieldset.meta-collection-item").Should().ContainSingle();
            cut.FindComponents<MetaFormRenderer>().Should().ContainSingle();
            published.Should().NotBeEmpty();
            published[^1].Should().ContainSingle();
            published[^1][0]["qty"].Should().Be("0");
        });
        cut.Find("button.meta-collection-remove").HasAttribute("disabled").Should().BeTrue();

        cut.Find("button.meta-collection-add").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("fieldset.meta-collection-item").Should().HaveCount(2);
            cut.FindComponents<MetaFormRenderer>().Should().HaveCount(2);
            published[^1].Should().HaveCount(2);
            cut.Find("button.meta-collection-add").HasAttribute("disabled").Should().BeTrue();
            cut.Find("button.meta-collection-add").GetAttribute("title").Should().Contain("최대 2개");
        });
    }

    [Fact]
    public void Removing_an_item_preserves_the_stable_key_of_the_remaining_item()
    {
        using var ctx = RadzenContext();
        List<Dictionary<string, object?>>? changed = null;
        var cut = Render(
            ctx,
            [
                new Dictionary<string, object?> { ["name"] = "첫째" },
                new Dictionary<string, object?> { ["name"] = "둘째" },
            ],
            items => changed = Clone(items));
        var before = cut.FindAll("fieldset.meta-collection-item")
            .Select(element => element.GetAttribute("data-item-key"))
            .ToArray();

        cut.FindAll("button.meta-collection-remove")[0].Click();

        cut.WaitForAssertion(() =>
        {
            var remaining = cut.FindAll("fieldset.meta-collection-item").Single();
            remaining.GetAttribute("data-item-key").Should().Be(before[1]);
            changed.Should().ContainSingle();
            changed![0]["name"].Should().Be("둘째");
        });
    }

    [Fact]
    public async Task Changing_one_item_does_not_contaminate_another_item_that_shared_the_input_reference()
    {
        using var ctx = RadzenContext();
        var shared = new Dictionary<string, object?> { ["name"] = "공유 원본", ["qty"] = "1" };
        List<Dictionary<string, object?>>? changed = null;
        var cut = Render(ctx, [shared, shared], items => changed = Clone(items));
        var forms = cut.FindComponents<MetaFormRenderer>();

        forms[0].Instance.Model.Should().NotBeSameAs(forms[1].Instance.Model);
        forms[0].Instance.Model.Should().NotBeSameAs(shared);

        forms[1].Instance.Model["name"] = "둘째만 변경";
        await cut.InvokeAsync(() => forms[1].Instance.ModelChanged.InvokeAsync(forms[1].Instance.Model));

        cut.WaitForAssertion(() =>
        {
            changed.Should().HaveCount(2);
            changed![0]["name"].Should().Be("공유 원본");
            changed[1]["name"].Should().Be("둘째만 변경");
            shared["name"].Should().Be("공유 원본");

            var updatedForms = cut.FindComponents<MetaFormRenderer>();
            updatedForms[0].Instance.Model["name"].Should().Be("공유 원본");
            updatedForms[1].Instance.Model["name"].Should().Be("둘째만 변경");
        });
    }

    [Fact]
    public void Disabled_reason_blocks_every_mutation_and_is_exposed_to_assistive_technology()
    {
        using var ctx = RadzenContext();
        const string reason = "품질 편집 권한이 없습니다.";
        var cut = ctx.Render<MetaCollectionEditor>(parameters => parameters
            .Add(component => component.Schema, Schema)
            .Add(component => component.Items,
            [
                new Dictionary<string, object?> { ["name"] = "읽기 전용" },
            ])
            .Add(component => component.DisabledReason, reason));

        var root = cut.Find("section.meta-collection-editor");
        var note = cut.Find("[role=note]");
        root.GetAttribute("aria-disabled").Should().Be("true");
        root.GetAttribute("aria-describedby").Should().Be(note.Id);
        note.TextContent.Should().Contain(reason);
        cut.Find("fieldset.meta-collection-item").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.meta-collection-add").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.meta-collection-remove").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.meta-collection-add").GetAttribute("aria-label").Should().Contain(reason);
        cut.FindComponents<MetaFormRenderer>().Single().Instance.Definition.Fields
            .Should().OnlyContain(field => field.ReadOnly);
    }

    [Fact]
    public void Collection_and_item_labels_have_unique_accessible_relationships()
    {
        using var ctx = RadzenContext();
        var cut = ctx.Render<MetaCollectionEditor>(parameters => parameters
            .Add(component => component.Schema, Schema)
            .Add(component => component.Items,
            [
                new Dictionary<string, object?> { ["name"] = "A" },
                new Dictionary<string, object?> { ["name"] = "B" },
            ])
            .Add(component => component.Label, "검사 샘플")
            .Add(component => component.ItemLabel, "샘플"));

        var root = cut.Find("section.meta-collection-editor");
        var titleId = root.GetAttribute("aria-labelledby");
        titleId.Should().NotBeNullOrWhiteSpace();
        cut.Find($"#{titleId}").TextContent.Should().Be("검사 샘플");
        var legends = cut.FindAll("legend").Select(legend => legend.TextContent).ToArray();
        legends.Should().HaveCount(2);
        legends[0].Should().Contain("샘플 1");
        legends[1].Should().Contain("샘플 2");
        cut.FindAll("button.meta-collection-remove")
            .Select(button => button.GetAttribute("aria-label"))
            .Should().OnlyHaveUniqueItems();

        var fieldLabels = cut.FindAll(".meta-field label");
        fieldLabels.Select(label => label.GetAttribute("for"))
            .Where(value => value is not null)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Indexed_collection_error_is_cascaded_to_the_matching_item_field()
    {
        using var ctx = RadzenContext();
        var cut = ctx.Render<MetaCollectionEditor>(parameters =>
        {
            parameters.Add(component => component.CollectionKey, "items");
            parameters.Add(component => component.Schema, Schema);
            parameters.Add(component => component.Items,
            [
                new Dictionary<string, object?> { ["name"] = string.Empty },
                new Dictionary<string, object?> { ["name"] = "정상" },
            ]);
            parameters.AddCascadingValue(
                "MetaFieldErrors",
                new Dictionary<string, string>
                {
                    ["items[0].name"] = "검사 항목 1: 이름은(는) 필수입니다.",
                });
        });

        cut.FindAll(".meta-field-error").Should().ContainSingle();
        cut.Find(".meta-field-error").TextContent.Should().Contain("항목 1").And.Contain("필수");
    }

    [Fact]
    public void Collection_level_limit_error_is_accessible_and_over_limit_items_remain_removable()
    {
        using var ctx = RadzenContext();
        var cut = ctx.Render<MetaCollectionEditor>(parameters =>
        {
            parameters.Add(component => component.CollectionKey, "items");
            parameters.Add(component => component.Schema, Schema);
            parameters.Add(component => component.Items,
            [
                new Dictionary<string, object?> { ["name"] = "A" },
                new Dictionary<string, object?> { ["name"] = "B" },
            ]);
            parameters.Add(component => component.MaxItems, 1);
            parameters.AddCascadingValue(
                "MetaFieldErrors",
                new Dictionary<string, string> { ["items"] = "항목 목록은 최대 1개까지 입력할 수 있습니다." });
        });

        cut.FindAll("fieldset.meta-collection-item").Should().HaveCount(2,
            "최대 개수를 넘긴 외부 모델도 숨기지 않고 삭제로 복구할 수 있어야 한다");
        var error = cut.Find(".meta-collection-error");
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().Contain("최대 1개");
        cut.Find("section.meta-collection-editor").GetAttribute("aria-describedby").Should().Contain(error.Id);
        cut.Find("button.meta-collection-add").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button.meta-collection-remove").Should().OnlyContain(button => !button.HasAttribute("disabled"));
    }

    private static BunitContext RadzenContext()
    {
        var context = new BunitContext();
        context.Services.AddRadzenComponents();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static IRenderedComponent<MetaCollectionEditor> Render(
        BunitContext context,
        List<Dictionary<string, object?>> items,
        Action<List<Dictionary<string, object?>>>? changed = null)
        => context.Render<MetaCollectionEditor>(parameters =>
        {
            parameters.Add(component => component.Schema, Schema);
            parameters.Add(component => component.Items, items);
            if (changed is not null)
                parameters.Add(component => component.ItemsChanged, changed);
        });

    private static List<Dictionary<string, object?>> Clone(
        IEnumerable<Dictionary<string, object?>> items)
        => items.Select(item => new Dictionary<string, object?>(item, item.Comparer)).ToList();
}
