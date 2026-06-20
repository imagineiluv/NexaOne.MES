using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// Phase 3/4 — 메타데이터 화면 런타임(/meta/{UiId}). 그리드(컬럼) + 명명 쿼리가 바인딩된 정의면
/// 파일 기반 쿼리 게이트웨이(ExecuteQueryAsync)로 행을 조회해 MetaGridRenderer로 렌더하고,
/// 폼(필드) 전용 정의면 쿼리를 치지 않고 폼만 렌더하는지(데이터 소스 분기)를 검증한다.
/// </summary>
public sealed class MetaScreenTests
{
    private static Mock<IScreenDefinitionProvider> Provider(string uiId, ScreenDefinition def)
    {
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(p => p.Get(uiId)).Returns(def);
        // MetaScreen 로드는 GetAsync 경로를 사용한다 — 동기 Get 셋업과 동일 결과로 미러링(거동 불변).
        provider.Setup(p => p.GetAsync(uiId, It.IsAny<CancellationToken>())).ReturnsAsync(def);
        return provider;
    }

    [Fact]
    public void Grid_definition_loads_rows_from_query_gateway_and_renders()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // 그리드 전용 정의: 폼 필드 없음 + 컬럼 메타 + 데이터 소스 쿼리(Q.Plants) 바인딩.
        var def = new ScreenDefinition("GRID1", "공장 목록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명") },
            QueryId: "Q.Plants");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>>
           {
               new() { ["PLANT_ID"] = "P-1", ["PLANT_NAME"] = "Plant One" },
           });

        ctx.Services.AddSingleton(Provider("GRID1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "GRID1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("공장 목록").And.Contain("공장 ID").And.Contain("Plant One");
            cut.FindAll("tbody tr").Count.Should().Be(1, "쿼리 결과 1행이 그리드로 렌더돼야 한다");
        }, TimeSpan.FromSeconds(2));

        // 그리드+쿼리 바인딩 화면은 명명 쿼리로 게이트웨이를 호출한다(저코드 조회 경로 end-to-end UI측).
        api.Verify(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Form_only_definition_renders_form_and_does_not_call_query_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // 폼 전용 정의: 컬럼/쿼리 없음 — 쿼리 게이트웨이를 치면 안 된다.
        var def = new ScreenDefinition("FORM1", "파라미터 입력",
            new FieldDefinition[] { new("name", "이름", FieldType.Text, Required: true) });

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("FORM1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "FORM1"));

        cut.Markup.Should().Contain("파라미터 입력").And.Contain("이름");
        cut.FindAll("button").Should().NotBeEmpty("폼 화면은 저장 버튼을 렌더해야 한다");
        cut.FindAll("tbody tr").Should().BeEmpty("폼 전용 화면은 그리드가 없어야 한다");

        api.Verify(a => a.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "데이터 소스 쿼리가 없는 폼 전용 화면은 쿼리 게이트웨이를 호출하지 않아야 한다");
    }

    [Fact]
    public void Save_with_SaveQueryId_posts_form_values_to_command_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // 저장(쓰기) 쿼리가 바인딩된 폼 — 필수 필드 1개.
        var def = new ScreenDefinition("SAVE1", "공장 등록",
            new FieldDefinition[] { new("plantName", "공장명", FieldType.Text, Required: true) },
            SaveQueryId: "MDM.CreatePlant");

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("SAVE1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "SAVE1"));

        // 필수 필드를 채운 뒤 저장 → command 게이트웨이로 전송.
        cut.Find("input").Change("플랜트1");
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            api.Verify(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()),
                Times.Once, "저장은 바인딩된 명명 쓰기쿼리로 폼 값을 전송해야 한다");
            cut.Markup.Should().Contain("저장됨");
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Save_blocks_and_does_not_call_gateway_when_required_field_empty()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = new ScreenDefinition("SAVE2", "공장 등록",
            new FieldDefinition[] { new("plantName", "공장명", FieldType.Text, Required: true) },
            SaveQueryId: "MDM.CreatePlant");

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("SAVE2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "SAVE2"));

        // 필수 필드를 비운 채 저장 → 검증 실패 메시지 + 게이트웨이 미호출.
        cut.Find("button").Click();

        cut.Markup.Should().Contain("필수");
        api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "필수 검증 실패 시 쓰기 게이트웨이를 호출하지 않아야 한다");
    }

    [Fact]
    public void Layout_executes_each_distinct_read_query_once_and_renders_grids()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = new ScreenDefinition("LAY1", "대시보드",
            Array.Empty<FieldDefinition>(),
            Layout: new RowNode
            {
                Children = new LayoutNode[]
                {
                    new GridWidget { QueryId = "Q.Plants", Columns = new GridColumnDefinition[] { new("PLANT_ID", "공장") } },
                    new GridWidget { QueryId = "Q.Lines", Columns = new GridColumnDefinition[] { new("LINE_ID", "라인") } },
                },
            });

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["PLANT_ID"] = "P-1" } });
        api.Setup(a => a.ExecuteQueryAsync("Q.Lines", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<Dictionary<string, object?>> { new() { ["LINE_ID"] = "L-1" } });

        ctx.Services.AddSingleton(Provider("LAY1", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY1"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("P-1").And.Contain("L-1");
            cut.FindAll("tbody tr").Count.Should().Be(2, "그리드 2개가 각자 1행씩 렌더");
        }, TimeSpan.FromSeconds(2));

        api.Verify(a => a.ExecuteQueryAsync("Q.Plants", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(a => a.ExecuteQueryAsync("Q.Lines", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Layout_command_button_posts_shared_model_to_command_gateway()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = new ScreenDefinition("LAY2", "등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new FieldWidget { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                    new ButtonWidget { Label = "저장", Command = "MDM.CreatePlant" },
                },
            });

        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(true);
        ctx.Services.AddSingleton(Provider("LAY2", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY2"));

        cut.Find("input").Change("플랜트1");
        cut.Find("button.layout-command").Click();

        cut.WaitForAssertion(() =>
            api.Verify(a => a.ExecuteCommandAsync("MDM.CreatePlant", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Layout_validation_blocks_command_when_required_field_empty()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var def = new ScreenDefinition("LAY3", "등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = new LayoutNode[]
                {
                    new FieldWidget { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                    new ButtonWidget { Label = "저장", Command = "MDM.CreatePlant" },
                },
            });

        var api = new Mock<IApiClient>();
        ctx.Services.AddSingleton(Provider("LAY3", def).Object);
        ctx.Services.AddSingleton(api.Object);

        var cut = ctx.RenderComponent<MetaScreen>(p => p.Add(c => c.UiId, "LAY3"));

        cut.Find("button.layout-command").Click();   // 필수 필드 비움

        cut.Markup.Should().Contain("필수");
        api.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never, "레이아웃 검증 실패 시 명령 게이트웨이를 호출하지 않아야 한다");
    }
}
