using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Pages.Meta;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

public sealed class MrpConversionMetaCommandDriverTests
{
    [Fact]
    public async Task Driver_exposes_pom_manage_but_rejects_row_level_fallback()
    {
        var driver = new MrpConversionMetaCommandDriver();
        var context = new MetaCommandExecutionContext("NX_MRP_PLANNING", "MES");

        driver.CommandIds.Should().ContainSingle(MrpConversionMetaCommands.Convert);
        driver.GetRequiredPermission(MrpConversionMetaCommands.Convert).Should().Be("pom:manage");
        driver.Commands.Should().ContainSingle(command =>
            command.Id == MrpConversionMetaCommands.Convert
            && command.RequiredPermission == "pom:manage"
            && command.ExecutionMode == MetaCommandExecutionMode.HostRequiredAggregate
            && command.Effect == MetaCommandEffect.Mutating);
        driver.CanExecute(MrpConversionMetaCommands.Convert, new Dictionary<string, object?>(), context)
            .CanExecute.Should().BeFalse();

        var result = await driver.ExecuteAsync(
            MrpConversionMetaCommands.Convert,
            new Dictionary<string, object?> { ["PLANNED_ORDER_ID"] = "MRP-1" },
            context);
        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(422);
        result.Error.Should().Contain("runId").And.Contain("plannedOrderIds").And.Contain("productionAssignments");

        var catalog = new MetaCommandDriverCatalog([driver]);
        catalog.CanExecute(MrpConversionMetaCommands.Convert, new Dictionary<string, object?>(), context)
            .DisabledReason.Should().Contain("전용 호스트");
        var catalogResult = await catalog.ExecuteAsync(
            MrpConversionMetaCommands.Convert,
            new Dictionary<string, object?>(),
            context);
        catalogResult.Success.Should().BeFalse();
        catalogResult.StatusCode.Should().Be(422);
        catalogResult.Error.Should().Contain("전용 호스트");
    }

    [Fact]
    public void Explicit_host_bulk_handler_precedes_catalog_row_fallback()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        ctx.Services.AddSingleton<DialogService>(new AcceptDialogService());

        var command = new BulkCommandDefinition("실오더 전환", MrpConversionMetaCommands.Convert);
        var definition = new ScreenDefinition(
            "NX_MRP_PLANNING",
            "MRP",
            Array.Empty<FieldDefinition>(),
            Columns: [new GridColumnDefinition("PLANNED_ORDER_ID", "제안")],
            QueryId: "POM.MrpPlannedOrderList",
            BulkCommands: [command]);
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(item => item.GetAsync("NX_MRP_PLANNING", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        var api = new Mock<IApiClient>();
        api.Setup(item => item.ExecuteQueryAsync(
                "POM.MrpPlannedOrderList", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Dictionary<string, object?> { ["PLANNED_ORDER_ID"] = "MRP-1" },
            ]);
        var catalog = new Mock<IMetaCommandDriverCatalog>();
        catalog.Setup(item => item.Contains(MrpConversionMetaCommands.Convert)).Returns(true);
        catalog.Setup(item => item.CanExecute(
                MrpConversionMetaCommands.Convert,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>()))
            .Returns(MetaCommandAvailability.Disabled("row fallback must not run"));

        var handled = 0;
        Func<BulkCommandDefinition, List<Dictionary<string, object?>>, Task<bool>> handler =
            (selectedCommand, rows) =>
            {
                selectedCommand.CommandQueryId.Should().Be(MrpConversionMetaCommands.Convert);
                rows.Should().ContainSingle();
                handled++;
                return Task.FromResult(false);
            };

        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalog.Object);
        var cut = ctx.RenderComponent<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, "NX_MRP_PLANNING")
            .Add(component => component.BridgeBulkHandler, handler));
        cut.WaitForAssertion(() => cut.FindAll(".rz-data-row").Should().ContainSingle());

        cut.FindAll(".meta-grid-toolbar button")
            .First(button => button.QuerySelector(".rzi")?.TextContent.Trim() == "checklist")
            .Click();
        cut.Find(".rz-data-row input[type=checkbox]").Change(true);
        cut.FindAll(".meta-grid-toolbar button")
            .First(button => button.TextContent.Contains("실오더 전환"))
            .Click();

        cut.WaitForAssertion(() => handled.Should().Be(1));
        catalog.Verify(item => item.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, object?>>(),
            It.IsAny<MetaCommandExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
