using Bunit;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// Phase 3 — 메타데이터 주도 폼 런타임 렌더러(MetaFormRenderer). ScreenDefinition.Fields(메타) +
/// Dictionary 모델을 받아 필드 Type별로 적합한 컨트롤을 그리고(체크박스/셀렉트/number·date·text input),
/// Required ' *' 표식·ReadOnly(readonly/disabled)·현재값 반영(checked/value/selected)을 검증한다.
/// 또한 입력 Change → ModelChanged 콜백이 갱신된 모델 Dictionary로 호출되는 양방향 바인딩을 보장한다.
/// (손코딩 .razor 없이 메타데이터로 입력 폼을 정의·렌더하는 저코드 입력 경로의 UI측)
/// </summary>
public sealed class MetaFormRendererTests
{
    // 단일 필드만 가진 폼 정의를 만드는 헬퍼 — 그리드/쿼리는 사용하지 않는 폼 전용 정의.
    private static ScreenDefinition FormWith(params FieldDefinition[] fields)
        => new("FORM", "테스트 폼", fields);

    private static IRenderedComponent<MetaFormRenderer> Render(
        TestContext ctx,
        ScreenDefinition def,
        Dictionary<string, object?>? model = null,
        Action<Dictionary<string, object?>>? onModelChanged = null)
        => ctx.RenderComponent<MetaFormRenderer>(p =>
        {
            p.Add(c => c.Definition, def);
            if (model is not null)
                p.Add(c => c.Model, model);
            if (onModelChanged is not null)
                p.Add(c => c.ModelChanged, onModelChanged);
        });

    // ── (a) Boolean → checkbox ─────────────────────────────────────────────

    [Fact]
    public void Boolean_field_renders_checkbox()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("active", "활성", FieldType.Boolean));
        var cut = Render(ctx, def);

        var input = cut.Find("input");
        input.GetAttribute("type").Should().Be("checkbox", "Boolean 필드는 checkbox로 렌더돼야 한다");
    }

    [Fact]
    public void Boolean_field_checked_when_model_value_is_true()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("active", "활성", FieldType.Boolean));
        var cut = Render(ctx, def, model: new() { ["active"] = true });

        // Model[key]=true 이면 checkbox는 checked 상태여야 한다.
        var input = cut.Find("input[type=checkbox]");
        input.HasAttribute("checked").Should().BeTrue("Model[key]=true 이면 checkbox는 checked 여야 한다");
    }

    [Fact]
    public void Boolean_field_unchecked_when_model_value_is_false_or_missing()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("active", "활성", FieldType.Boolean));

        // 값이 false 인 경우.
        var cutFalse = Render(ctx, def, model: new() { ["active"] = false });
        cutFalse.Find("input[type=checkbox]").HasAttribute("checked")
            .Should().BeFalse("Model[key]=false 이면 checkbox는 unchecked 여야 한다");

        // 키 자체가 없는 경우(GetBool은 false 반환).
        var cutMissing = Render(ctx, def, model: new());
        cutMissing.Find("input[type=checkbox]").HasAttribute("checked")
            .Should().BeFalse("모델에 키가 없으면 checkbox는 unchecked 여야 한다");
    }

    [Fact]
    public void Boolean_change_invokes_ModelChanged_with_true()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        Dictionary<string, object?>? captured = null;
        var def = FormWith(new FieldDefinition("active", "활성", FieldType.Boolean));
        var cut = Render(ctx, def, model: new() { ["active"] = false },
            onModelChanged: m => captured = m);

        // checkbox 체크 → ChangeEventArgs.Value=true 가 Set 으로 전달돼 모델이 갱신되고 콜백이 호출된다.
        cut.Find("input[type=checkbox]").Change(true);

        captured.Should().NotBeNull("checkbox 변경 시 ModelChanged 콜백이 호출돼야 한다");
        captured!["active"].Should().Be(true, "체크된 값(true)이 모델에 반영돼야 한다");
    }

    // ── (b) Select → options + 현재값 selected ─────────────────────────────

    [Fact]
    public void Select_field_renders_options_for_each_choice()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("color", "색상", FieldType.Select,
            Options: new[] { "Red", "Green", "Blue" }));
        var cut = Render(ctx, def);

        cut.Find("select").Should().NotBeNull("Select 필드는 select 요소로 렌더돼야 한다");
        var options = cut.FindAll("select option");
        options.Count.Should().Be(3, "Options 개수만큼 option이 렌더돼야 한다");
        cut.Markup.Should().Contain("Red").And.Contain("Green").And.Contain("Blue");
    }

    [Fact]
    public void Select_field_marks_current_value_as_selected()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("color", "색상", FieldType.Select,
            Options: new[] { "Red", "Green", "Blue" }));
        var cut = Render(ctx, def, model: new() { ["color"] = "Green" });

        // 현재 모델값과 일치하는 option만 selected 여야 한다.
        var options = cut.FindAll("select option");
        var green = options.Single(o => o.TextContent == "Green");
        green.HasAttribute("selected").Should().BeTrue("현재 모델값과 일치하는 option은 selected 여야 한다");

        options.Single(o => o.TextContent == "Red").HasAttribute("selected")
            .Should().BeFalse("일치하지 않는 option은 selected가 아니어야 한다");
        options.Single(o => o.TextContent == "Blue").HasAttribute("selected")
            .Should().BeFalse("일치하지 않는 option은 selected가 아니어야 한다");
    }

    [Fact]
    public void Select_field_with_null_options_renders_empty_select_without_error()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Options=null 일 때 Array.Empty 폴백으로 option 없이 select만 그려져야 한다(예외 없이).
        var def = FormWith(new FieldDefinition("color", "색상", FieldType.Select));
        var cut = Render(ctx, def);

        cut.Find("select").Should().NotBeNull("Options가 null이어도 select는 렌더돼야 한다");
        cut.FindAll("select option").Should().BeEmpty("Options가 null이면 option은 없어야 한다");
    }

    [Fact]
    public void Select_change_invokes_ModelChanged_with_selected_value()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        Dictionary<string, object?>? captured = null;
        var def = FormWith(new FieldDefinition("color", "색상", FieldType.Select,
            Options: new[] { "Red", "Green", "Blue" }));
        var cut = Render(ctx, def, model: new() { ["color"] = "Red" },
            onModelChanged: m => captured = m);

        cut.Find("select").Change("Blue");

        captured.Should().NotBeNull("select 변경 시 ModelChanged 콜백이 호출돼야 한다");
        captured!["color"].Should().Be("Blue", "선택한 option 값이 모델에 반영돼야 한다");
    }

    // ── (c) Number / Date → input type ────────────────────────────────────

    [Fact]
    public void Number_field_renders_input_type_number()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("qty", "수량", FieldType.Number));
        var cut = Render(ctx, def);

        cut.Find("input").GetAttribute("type").Should().Be("number", "Number 필드는 input type=number 여야 한다");
    }

    [Fact]
    public void Date_field_renders_input_type_date()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("when", "일자", FieldType.Date));
        var cut = Render(ctx, def);

        cut.Find("input").GetAttribute("type").Should().Be("date", "Date 필드는 input type=date 여야 한다");
    }

    [Fact]
    public void Text_field_renders_input_type_text_and_reflects_current_value()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text));
        var cut = Render(ctx, def, model: new() { ["name"] = "홍길동" });

        var input = cut.Find("input");
        input.GetAttribute("type").Should().Be("text", "Text 필드는 input type=text 여야 한다");
        input.GetAttribute("value").Should().Be("홍길동", "현재 모델값이 input value에 반영돼야 한다");
    }

    [Fact]
    public void Text_change_invokes_ModelChanged_with_new_string()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        Dictionary<string, object?>? captured = null;
        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text));
        var cut = Render(ctx, def, model: new(), onModelChanged: m => captured = m);

        cut.Find("input").Change("새이름");

        captured.Should().NotBeNull("text 입력 변경 시 ModelChanged 콜백이 호출돼야 한다");
        captured!["name"].Should().Be("새이름", "입력 문자열이 모델에 반영돼야 한다");
    }

    // ── (d) Required ' *' 라벨 · ReadOnly ─────────────────────────────────

    [Fact]
    public void Required_field_label_has_asterisk_suffix()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text, Required: true));
        var cut = Render(ctx, def);

        var label = cut.Find("label");
        label.TextContent.Should().Be("이름 *", "Required 필드 라벨은 ' *' 접미사를 가져야 한다");
    }

    [Fact]
    public void Optional_field_label_has_no_asterisk()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text, Required: false));
        var cut = Render(ctx, def);

        cut.Find("label").TextContent.Should().Be("이름", "Required가 아닌 필드 라벨에는 ' *'가 없어야 한다");
    }

    [Fact]
    public void ReadOnly_text_field_has_readonly_attribute()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text, ReadOnly: true));
        var cut = Render(ctx, def);

        cut.Find("input").HasAttribute("readonly")
            .Should().BeTrue("ReadOnly 텍스트 필드는 readonly 속성을 가져야 한다");
    }

    [Fact]
    public void Editable_text_field_has_no_readonly_attribute()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text, ReadOnly: false));
        var cut = Render(ctx, def);

        cut.Find("input").HasAttribute("readonly")
            .Should().BeFalse("편집 가능한 텍스트 필드에는 readonly 속성이 없어야 한다");
    }

    [Fact]
    public void ReadOnly_boolean_field_is_disabled()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("active", "활성", FieldType.Boolean, ReadOnly: true));
        var cut = Render(ctx, def);

        cut.Find("input[type=checkbox]").HasAttribute("disabled")
            .Should().BeTrue("ReadOnly Boolean 필드는 disabled 속성을 가져야 한다");
    }

    [Fact]
    public void ReadOnly_select_field_is_disabled()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(new FieldDefinition("color", "색상", FieldType.Select,
            ReadOnly: true, Options: new[] { "Red", "Green" }));
        var cut = Render(ctx, def);

        cut.Find("select").HasAttribute("disabled")
            .Should().BeTrue("ReadOnly Select 필드는 disabled 속성을 가져야 한다");
    }

    // ── (e) 다중 필드 · 라벨/필드 구조 ────────────────────────────────────

    [Fact]
    public void Renders_one_field_block_per_definition_field()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = FormWith(
            new FieldDefinition("name", "이름", FieldType.Text),
            new FieldDefinition("qty", "수량", FieldType.Number),
            new FieldDefinition("active", "활성", FieldType.Boolean));
        var cut = Render(ctx, def);

        cut.FindAll(".meta-field").Count.Should().Be(3, "필드 정의 개수만큼 필드 블록이 렌더돼야 한다");
        cut.FindAll("label").Count.Should().Be(3, "각 필드는 라벨을 가져야 한다");
        cut.Markup.Should().Contain("이름").And.Contain("수량").And.Contain("활성");
    }

    [Fact]
    public void Change_on_one_field_preserves_other_fields_in_model()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        Dictionary<string, object?>? captured = null;
        var def = FormWith(
            new FieldDefinition("name", "이름", FieldType.Text),
            new FieldDefinition("qty", "수량", FieldType.Number));
        // 두 키가 모두 있는 모델 — 한 필드 변경이 다른 키를 보존하는지(동일 Dictionary 갱신) 확인.
        var cut = Render(ctx, def,
            model: new() { ["name"] = "기존이름", ["qty"] = "5" },
            onModelChanged: m => captured = m);

        // 수량 input(2번째)을 변경.
        cut.FindAll("input")[1].Change("10");

        captured.Should().NotBeNull();
        captured!["qty"].Should().Be("10", "변경한 필드 값이 갱신돼야 한다");
        captured!["name"].Should().Be("기존이름", "다른 필드 값은 모델에 보존돼야 한다");
    }
}
