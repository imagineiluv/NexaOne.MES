using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

/// <summary>등록 목적 메타 화면이 목록과 신규 입력을 분리하는 UX 계약을 검증한다.</summary>
public sealed class MetaScreenRegisterTests
{
    [Fact]
    public void Register_screen_uses_header_modal_and_bridge_without_row_overwriting_add_model()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var definition = new ScreenDefinition(
            "QMS_REGISTER",
            "검사 결과 등록",
            [new FieldDefinition("inspectionId", "검사 ID", Required: true)],
            [new GridColumnDefinition("INSPECTION_ID", "검사 ID")],
            QueryId: "QMS.InspectionHistory",
            SaveQueryId: QmsInspectionMetaCommands.RecordIncoming,
            Purpose: ScreenPurpose.Register);
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.Get("QMS_REGISTER")).Returns(definition);
        provider.Setup(item => item.GetAsync("QMS_REGISTER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);

        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecuteQueryAsync(
                "QMS.InspectionHistory", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Dictionary<string, object?>
                {
                    ["INSPECTION_ID"] = "HISTORY-001"
                }
            ]);

        IReadOnlyDictionary<string, object?>? capturedParameters = null;
        var catalog = new Mock<IMetaCommandDriverCatalog>();
        catalog.Setup(item => item.Contains(QmsInspectionMetaCommands.RecordIncoming)).Returns(true);
        catalog.Setup(item => item.CanExecute(
                QmsInspectionMetaCommands.RecordIncoming,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>()))
            .Returns(MetaCommandAvailability.Enabled);
        catalog.Setup(item => item.ExecuteAsync(
                QmsInspectionMetaCommands.RecordIncoming,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, object?>, MetaCommandExecutionContext, CancellationToken>(
                (_, parameters, _, _) => capturedParameters =
                    new Dictionary<string, object?>(parameters, StringComparer.Ordinal))
            .ReturnsAsync(MetaCommandResult.Succeeded());

        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalog.Object);

        var cut = ctx.RenderComponent<MetaScreen>(parameters =>
            parameters.Add(component => component.UiId, "QMS_REGISTER"));

        cut.WaitForAssertion(() => cut.FindAll(".rz-data-row").Should().HaveCount(1));
        cut.Find(".meta-purpose-label").TextContent.Should().Contain("등록");
        cut.FindAll(".meta-form-card").Should().BeEmpty(
            "등록 목적 화면은 목록 위에 중복 인라인 폼을 렌더하지 않아야 한다");
        cut.Find("button.meta-primary-action").TextContent.Should().Contain("신규 등록");
        cut.FindAll(".meta-grid-toolbar button")
            .Should().NotContain(button => button.TextContent.Contains("추가", StringComparison.Ordinal),
                "목록 우선 등록 화면은 헤더와 그리드에 등록 버튼을 중복 노출하지 않아야 한다");

        cut.Find("button.meta-primary-action").Click();
        cut.WaitForAssertion(() => cut.FindAll(".nx-modal").Should().ContainSingle());
        cut.Find(".nx-modal .meta-field input").Change("NEW-INSPECTION");

        // 등록 화면의 목록 행을 선택해도 모달 전용 모델은 최근 이력 값으로 바뀌면 안 된다.
        var grid = cut.FindComponent<MetaGridRenderer>();
        cut.InvokeAsync(() => grid.Instance.OnRowSelect.InvokeAsync(
            new Dictionary<string, object?> { ["INSPECTION_ID"] = "HISTORY-001" }));
        cut.Find(".nx-modal .meta-field input").GetAttribute("value")
            .Should().Be("NEW-INSPECTION");

        cut.Find(".nx-modal .nx-modal-save").Click();

        cut.WaitForAssertion(() =>
        {
            capturedParameters.Should().NotBeNull();
            cut.FindAll(".nx-modal").Should().BeEmpty();
        });
        capturedParameters!["inspectionId"].Should().Be("NEW-INSPECTION");
        catalog.Verify(item => item.ExecuteAsync(
            QmsInspectionMetaCommands.RecordIncoming,
            It.IsAny<IReadOnlyDictionary<string, object?>>(),
            It.IsAny<MetaCommandExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(client => client.ExecuteCommandAsync(
            It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never,
            "bridge 저장은 raw command API가 아니라 메타 명령 카탈로그를 경유해야 한다");
    }
}
