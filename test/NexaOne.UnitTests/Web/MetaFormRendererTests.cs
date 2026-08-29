using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// Phase 3 — 메타데이터 주도 폼 런타임 렌더러(MetaFormRenderer, Radzen 입력 기반). ScreenDefinition.Fields
/// 메타를 받아 필드 Type별로 적합한 Radzen 컨트롤(TextBox/Numeric/DatePicker/CheckBox/DropDown)을 그리고,
/// Required ' *' 표식·필드 블록 구조·현재값 반영을 검증한다. 입력 Change→ModelChanged 양방향은 Radzen
/// 내부 이벤트라 bUnit으로 직접 트리거하기 어려워(브라우저 스모크로 검증) 여기서는 렌더 계약을 잠근다.
/// </summary>
public sealed class MetaFormRendererTests
{
    private static ScreenDefinition FormWith(params FieldDefinition[] fields)
        => new("FORM", "테스트 폼", fields);

    private static TestContext RadzenContext()
    {
        var ctx = new TestContext();
        ctx.Services.AddRadzenComponents();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static IRenderedComponent<MetaFormRenderer> Render(
        TestContext ctx, ScreenDefinition def, Dictionary<string, object?>? model = null)
        => ctx.RenderComponent<MetaFormRenderer>(p =>
        {
            p.Add(c => c.Definition, def);
            if (model is not null) p.Add(c => c.Model, model);
        });

    [Fact]
    public void English_mode_localizes_form_and_field_labels_with_resource_first_fallback()
    {
        using var ctx = RadzenContext();
        var ui = new NexaOne.Web.Services.UiTextService();
        ui.Load("EnUs", new Dictionary<string, string>
        {
            ["screen.FORM.title"] = "Inventory Form",
            ["field.STOCK_QTY"] = "On-hand Quantity",
        });
        ctx.Services.AddSingleton(ui);

        var cut = Render(ctx, FormWith(
            new FieldDefinition("STOCK_QTY", "현재고", FieldType.Number),
            new FieldDefinition("RECEIVED_AT", "입고일시", FieldType.Date)));

        cut.Find(".meta-form").GetAttribute("aria-label").Should().Be("Inventory Form");
        cut.FindAll(".meta-field > label").Select(label => label.TextContent.Trim())
            .Should().ContainInOrder("On-hand Quantity", "Received At")
            .And.NotContain("현재고")
            .And.NotContain("입고일시");
    }

    // ── 필드 타입 → Radzen 컨트롤 매핑(우리가 소유한 switch) ──────────────────

    [Theory]
    [InlineData(FieldType.Text, "rz-textbox")]
    [InlineData(FieldType.Number, "rz-numeric")]
    [InlineData(FieldType.Date, "rz-datepicker")]
    [InlineData(FieldType.Boolean, "rz-chkbox")]
    public void Field_type_renders_matching_radzen_control(FieldType type, string rzClass)
    {
        using var ctx = RadzenContext();
        var cut = Render(ctx, FormWith(new FieldDefinition("f", "필드", type)));
        cut.Markup.Should().Contain(rzClass, $"{type} 필드는 {rzClass} Radzen 컨트롤로 렌더돼야 한다");
    }

    [Fact]
    public void Select_field_renders_dropdown_with_options()
    {
        using var ctx = RadzenContext();
        var def = FormWith(new FieldDefinition("color", "색상", FieldType.Select,
            Options: new[] { "Red", "Green", "Blue" }));
        var cut = Render(ctx, def);

        cut.Markup.Should().Contain("rz-dropdown", "Select 필드는 RadzenDropDown으로 렌더돼야 한다");
    }

    [Fact]
    public void Status_select_localizes_static_labels_but_keeps_contract_values()
    {
        using var ctx = RadzenContext();
        var values = new[] { "Draft", "Confirmed", "Producing", "Delivered", "Closed" };
        var cut = Render(ctx, FormWith(new FieldDefinition(
            "status", "상태", FieldType.Select, Options: values)));

        var data = cut.FindComponent<Radzen.Blazor.RadzenDropDown<string>>().Instance.Data;
        data.Should().NotBeNull();
        var options = data!.Cast<MetaFieldOption>().ToList();

        options.Select(option => option.Value).Should().Equal(values,
            "API/쿼리로 전달되는 상태 계약 값은 번역하지 않아야 한다");
        options.Select(option => option.Label).Should()
            .Equal("초안", "확정", "생산 중", "납품 완료", "마감");
    }

    [Fact]
    public void English_status_select_uses_natural_labels_even_before_resource_sync()
    {
        using var ctx = RadzenContext();
        var ui = new NexaOne.Web.Services.UiTextService();
        ui.Load("EnUs", new Dictionary<string, string>());
        ctx.Services.AddSingleton(ui);
        var cut = Render(ctx, FormWith(new FieldDefinition(
            "ORDER_STATUS", "상태", FieldType.Select,
            Options: new[] { "Draft", "Confirmed", "Producing", "Delivered", "Closed" })));

        var data = cut.FindComponent<Radzen.Blazor.RadzenDropDown<string>>().Instance.Data;
        data.Should().NotBeNull();
        var options = data!.Cast<MetaFieldOption>().ToList();

        options.Select(option => option.Label).Should()
            .Equal(new[] { "Draft", "Confirmed", "Producing", "Delivered", "Closed" },
                "영문 리소스 배포 시차가 있어도 한국어 상태명이 섞이면 안 된다");
    }

    [Fact]
    public void Select_field_with_null_options_renders_without_error()
    {
        using var ctx = RadzenContext();
        var cut = Render(ctx, FormWith(new FieldDefinition("color", "색상", FieldType.Select)));
        cut.Markup.Should().Contain("rz-dropdown", "Options가 null이어도 드롭다운은 예외 없이 렌더돼야 한다");
    }

    // ── 구조 · Required · 값 반영 ────────────────────────────────────────────

    [Fact]
    public void Required_field_label_has_styled_asterisk_marker()
    {
        using var ctx = RadzenContext();
        var cut = Render(ctx, FormWith(new FieldDefinition("name", "이름", FieldType.Text, Required: true)));
        // 필수 표시는 라벨 텍스트 뒤 .req(빨강) 스팬의 '*'. (라벨 전체 TextContent = "이름*")
        cut.Find("label").TextContent.Should().StartWith("이름").And.EndWith("*");
        cut.Find("label span.req").TextContent.Should().Be("*", "필수 마커는 .req 스팬으로 렌더된다");
    }

    [Fact]
    public void Optional_field_label_has_no_asterisk()
    {
        using var ctx = RadzenContext();
        var cut = Render(ctx, FormWith(new FieldDefinition("name", "이름", FieldType.Text, Required: false)));
        cut.Find("label").TextContent.Should().Be("이름", "Required가 아닌 필드 라벨에는 ' *'가 없어야 한다");
    }

    [Fact]
    public void Date_field_accepts_database_datetime_text_when_editing_a_selected_row()
    {
        using var ctx = RadzenContext();
        var cut = Render(
            ctx,
            FormWith(new FieldDefinition("planEndDate", "납기 예정일", FieldType.Date)),
            new Dictionary<string, object?> { ["planEndDate"] = "2026-07-31 00:00:00" });

        var picker = cut.FindComponent<Radzen.Blazor.RadzenDatePicker<DateTime?>>();
        picker.Instance.Value.Should().Be(new DateTime(2026, 7, 31),
            "DB datetime 문자열도 관리 폼의 날짜 선택기에 복원돼야 한다");
    }

    // (값 반영/Change→ModelChanged 양방향은 Radzen 내부 렌더·이벤트라 bUnit 정적 마크업엔 안 드러난다.
    //  MetaScreen 행선택 테스트가 값 반영을, 실브라우저 스모크가 편집 왕복을 실증한다.)

    [Fact]
    public void Renders_one_field_block_per_definition_field()
    {
        using var ctx = RadzenContext();
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
    public void Hidden_system_field_is_not_rendered_as_user_input()
    {
        using var ctx = RadzenContext();
        var cut = Render(ctx, FormWith(
            new FieldDefinition("name", "이름"),
            new FieldDefinition(
                "idempotencyKey",
                "멱등 키",
                Hidden: true,
                ValueGenerator: FieldValueGenerator.UuidV4)));

        cut.FindAll(".meta-field").Should().ContainSingle();
        cut.Markup.Should().Contain("이름").And.NotContain("멱등 키");
    }

    [Fact]
    public void Field_error_renders_inline_message()
    {
        using var ctx = RadzenContext();
        var def = FormWith(new FieldDefinition("name", "이름", FieldType.Text));
        var cut = ctx.RenderComponent<MetaFormRenderer>(p =>
        {
            p.Add(c => c.Definition, def);
            // 캐스케이딩 검증 오류 맵 — 필드 아래 인라인 메시지(.meta-field-error)로 표시(P2-7).
            p.AddCascadingValue("MetaFieldErrors", new Dictionary<string, string> { ["name"] = "이름은(는) 필수입니다." });
        });

        cut.Find(".meta-field-error").TextContent.Should().Contain("필수");
        cut.Find(".meta-field").ClassList.Should().Contain("has-error");
        cut.Find("input").GetAttribute("aria-invalid").Should().Be("true");
    }

    [Fact]
    public void Readonly_required_field_exposes_state_classes_and_accessibility_attributes()
    {
        using var ctx = RadzenContext();
        var cut = Render(ctx, FormWith(new FieldDefinition(
            "orderNo", "수주 번호", FieldType.Text, Required: true, ReadOnly: true)));

        var field = cut.Find(".meta-field");
        field.ClassList.Should().Contain("is-required").And.Contain("is-readonly")
            .And.Contain("meta-field--text");
        cut.Find("input").GetAttribute("aria-required").Should().Be("true");
        cut.Find("input").GetAttribute("aria-readonly").Should().Be("true");
        cut.Find(".meta-field-readonly").GetAttribute("aria-label").Should().Be("읽기 전용");
    }
}
