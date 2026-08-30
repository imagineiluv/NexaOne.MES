using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

public sealed class MetaScreenDeleteCommandTests
{
    [Fact]
    public void Named_delete_still_uses_the_command_gateway()
    {
        using var ctx = CreateContext();
        var definition = DeleteScreen("NAMED_DELETE", "TEST.Delete");
        var provider = Provider(definition);
        var api = new Mock<IApiClient>();
        api.Setup(item => item.ExecuteQueryAsync(
                "TEST.List", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rows("ROW-1"));

        Dictionary<string, object?>? submitted = null;
        api.Setup(item => item.ExecuteCommandAsync(
                "TEST.Delete", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>((_, parameters, _) =>
                submitted = parameters as Dictionary<string, object?>)
            .ReturnsAsync(true);
        var catalog = new Mock<IMetaCommandDriverCatalog>();

        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalog.Object);
        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId));

        SelectRowsAndDelete(cut, 0);

        cut.WaitForAssertion(() =>
        {
            api.Verify(item => item.ExecuteCommandAsync(
                "TEST.Delete", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once);
            submitted.Should().NotBeNull();
            submitted!["ID"].Should().Be("ROW-1");
            submitted["id"].Should().Be("ROW-1");
        });
        catalog.Verify(item => item.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, object?>>(),
            It.IsAny<MetaCommandExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Bridge_delete_invokes_the_registered_driver_once_per_selected_row()
    {
        using var ctx = CreateContext();
        const string command = "bridge:test.delete";
        var definition = DeleteScreen("BRIDGE_DELETE", command);
        var provider = Provider(definition);
        var api = new Mock<IApiClient>();
        api.Setup(item => item.ExecuteQueryAsync(
                "TEST.List", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rows("ROW-1", "ROW-2"));

        var executed = new List<(string Id, string UiId, string Channel)>();
        var catalog = new Mock<IMetaCommandDriverCatalog>();
        catalog.Setup(item => item.Contains(command)).Returns(true);
        catalog.Setup(item => item.CanExecute(
                command,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>()))
            .Returns(MetaCommandAvailability.Enabled);
        catalog.Setup(item => item.ExecuteAsync(
                command,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, object?>, MetaCommandExecutionContext, CancellationToken>(
                (_, parameters, context, _) => executed.Add((
                    parameters["ID"]?.ToString() ?? string.Empty,
                    context.UiId,
                    context.ClientChannel)))
            .ReturnsAsync(MetaCommandResult.Succeeded());

        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalog.Object);
        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, definition.UiId)
            .Add(component => component.ClientChannel, "POP"));

        SelectRowsAndDelete(cut, 0, 1);

        cut.WaitForAssertion(() => executed.Should().BeEquivalentTo(
        [
            ("ROW-1", "BRIDGE_DELETE", "POP"),
            ("ROW-2", "BRIDGE_DELETE", "POP"),
        ]));
        api.Verify(item => item.ExecuteCommandAsync(
            command, It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        ctx.Services.AddSingleton<DialogService>(new AcceptDialogService());
        return ctx;
    }

    private static ScreenDefinition DeleteScreen(string uiId, string deleteQueryId)
        => new(
            uiId,
            "Delete contract",
            Array.Empty<FieldDefinition>(),
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "TEST.List",
            DeleteQueryId: deleteQueryId);

    private static Mock<IScreenDefinitionProvider> Provider(ScreenDefinition definition)
    {
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.Get(definition.UiId)).Returns(definition);
        provider.Setup(item => item.GetAsync(definition.UiId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        return provider;
    }

    private static List<Dictionary<string, object?>> Rows(params string[] ids)
        => ids.Select(id => new Dictionary<string, object?> { ["ID"] = id }).ToList();

    private static void SelectRowsAndDelete(IRenderedComponent<MetaScreen> cut, params int[] indexes)
    {
        cut.WaitForAssertion(() => cut.FindAll(".rz-data-row").Count.Should().BeGreaterThan(0));
        cut.FindAll(".meta-grid-toolbar button")
            .First(button => button.QuerySelector(".rzi")?.TextContent.Trim() == "checklist")
            .Click();

        foreach (var index in indexes)
            cut.FindAll(".rz-data-row input[type=checkbox]")[index].Change(true);

        cut.FindAll(".meta-grid-toolbar button")
            .First(button => button.QuerySelector(".rzi")?.TextContent.Trim() == "delete")
            .Click();
    }

    private sealed class AcceptDialogService : DialogService
    {
        public AcceptDialogService() : base(null!, null!) { }

        public override Task<bool?> Confirm(
            string message = "Confirm",
            string title = "Confirm",
            ConfirmOptions? options = null,
            CancellationToken? cancellationToken = null)
            => Task.FromResult<bool?>(true);
    }
}
