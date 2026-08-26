using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

public sealed class MetaScreenActiveSurfaceTests
{
    [Fact]
    public void Layout_mode_does_not_execute_hidden_flat_query_count_or_field_options()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var definition = new ScreenDefinition(
            "LAYOUT_ACTIVE_SURFACE",
            "Layout active surface",
            [new FieldDefinition("hidden", "Hidden", OptionsQueryId: "Q.HiddenFieldOptions")],
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "Q.HiddenFlat",
            CountQueryId: "Q.HiddenCount",
            Layout: new TextWidget { Text = "Visible layout body" });
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.Get(definition.UiId)).Returns(definition);
        provider.Setup(item => item.GetAsync(definition.UiId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        var api = new Mock<IApiClient>();

        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        var cut = ctx.RenderComponent<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Visible layout body"));
        api.Verify(item => item.ExecuteQueryAsync(
            "Q.HiddenFlat", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(item => item.ExecuteQueryPagedAsync(
            "Q.HiddenFlat",
            It.IsAny<object?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(item => item.ExecuteQueryAsync(
            "Q.HiddenCount", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(item => item.ExecuteQueryAsync(
            "Q.HiddenFieldOptions", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
