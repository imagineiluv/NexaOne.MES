using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;
using NexaOne.Web.Pages.Meta;
using Radzen;

namespace NexaOne.UnitTests.Web;

public sealed class PomWorkOrderMetaCommandDriverTests
{
    private static readonly MetaCommandExecutionContext MesContext =
        new("FACTORY_PPM_WORK_ORDER", "MES", null);

    private static readonly MetaCommandExecutionContext MobileContext =
        new("POM_MOBILE_WORK_EXECUTION", "MOBILE", "PDA-07");

    [Fact]
    public async Task Create_maps_serial_route_registration_to_typed_api()
    {
        PomWorkOrderCreateRequest? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.CreatePomWorkOrderAsync(
                It.IsAny<PomWorkOrderCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PomWorkOrderCreateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PomWorkOrderActionResult(Dto(version: 1), null, 200));
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);
        var form = CreateForm("SerialRoute");
        form["routingId"] = "RT-FINAL-01";

        var result = await driver.ExecuteAsync(PomWorkOrderMetaCommands.Create, form, MobileContext);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.RoutingScope.Should().Be("SerialRoute");
        captured.RoutingId.Should().Be("RT-FINAL-01");
        captured.RoutingStepNo.Should().BeNull();
        captured.ProcessId.Should().BeNull();
        captured.PlanQty.Should().Be(100m);
    }

    [Theory]
    [InlineData("Operation", "공정 ID")]
    [InlineData("SerialRoute", "제품 라우팅 ID")]
    [InlineData("Unknown", "Unbound")]
    public void Create_rejects_invalid_scope_specific_input(string scope, string expectedReason)
    {
        var driver = new PomWorkOrderMetaCommandDriver(new Mock<IApiClient>().Object);
        var form = CreateForm(scope);
        if (scope == "Operation")
        {
            form["routingId"] = "RT-01";
            form["routingStepNo"] = 10;
        }

        var availability = driver.CanExecute(PomWorkOrderMetaCommands.Create, form, MobileContext);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain(expectedReason);
    }

    [Fact]
    public async Task Start_maps_version_channel_device_and_uses_stable_idempotency_key()
    {
        var requests = new List<PomWorkOrderActionRequest>();
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecutePomWorkOrderActionAsync(
                "start", "WO-100", It.IsAny<PomWorkOrderActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PomWorkOrderActionRequest, CancellationToken>((_, _, request, _) => requests.Add(request))
            .ReturnsAsync(new PomWorkOrderActionResult(Dto(version: 4), null, 200));
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);
        var row = Row(status: "Released", version: 3);

        (await driver.ExecuteAsync(PomWorkOrderMetaCommands.Start, row, MobileContext)).Success.Should().BeTrue();
        (await driver.ExecuteAsync(PomWorkOrderMetaCommands.Start, row, MobileContext)).Success.Should().BeTrue();

        requests.Should().HaveCount(2);
        requests[0].ExpectedVersion.Should().Be(3);
        requests[0].ClientChannel.Should().Be("MOBILE");
        requests[0].DeviceId.Should().Be("PDA-07");
        requests[0].IdempotencyKey.Should().Be(requests[1].IdempotencyKey,
            "같은 버전의 응답 유실 재시도는 서버의 동일 실행을 재생해야 한다");
        requests[0].IdempotencyKey.Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Report_sends_absolute_complete_and_scrap_totals_and_version_changes_key()
    {
        var requests = new List<PomWorkOrderActionRequest>();
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecutePomWorkOrderActionAsync(
                "report", "WO-100", It.IsAny<PomWorkOrderActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PomWorkOrderActionRequest, CancellationToken>((_, _, request, _) => requests.Add(request))
            .ReturnsAsync(new PomWorkOrderActionResult(Dto(version: 6), null, 200));
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);

        var v5 = Row(status: "Started", version: 5, complete: 7m, scrap: 2m);
        var v6 = Row(status: "Started", version: 6, complete: 7m, scrap: 2m);
        await driver.ExecuteAsync(PomWorkOrderMetaCommands.Report, v5, MobileContext);
        await driver.ExecuteAsync(PomWorkOrderMetaCommands.Report, v6, MobileContext);

        requests[0].GoodQty.Should().Be(7m, "COMPLETE_QTY는 양품 누계의 fallback이다");
        requests[0].DefectQty.Should().Be(2m, "SCRAP_QTY는 불량 누계의 fallback이다");
        requests[0].IdempotencyKey.Should().NotBe(requests[1].IdempotencyKey,
            "성공 후 VERSION_NO가 바뀌면 새 작업으로 실행해야 한다");
    }

    [Fact]
    public async Task Conflict_status_and_server_reason_are_preserved()
    {
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecutePomWorkOrderActionAsync(
                "start", "WO-100", It.IsAny<PomWorkOrderActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PomWorkOrderActionResult(null, "Current version: 9.", 409));
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(
            PomWorkOrderMetaCommands.Start, Row(status: "Released", version: 3), MobileContext);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("Current version: 9.");
    }

    [Theory]
    [InlineData(PomWorkOrderMetaCommands.Release, "release", "Created")]
    [InlineData(PomWorkOrderMetaCommands.Cancel, "cancel", "Released")]
    public async Task Management_action_maps_version_and_mes_channel_to_typed_api(
        string commandId,
        string expectedAction,
        string status)
    {
        string? capturedAction = null;
        PomWorkOrderActionRequest? capturedRequest = null;
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecutePomWorkOrderActionAsync(
                It.IsAny<string>(), "WO-100", It.IsAny<PomWorkOrderActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PomWorkOrderActionRequest, CancellationToken>((action, _, request, _) =>
            {
                capturedAction = action;
                capturedRequest = request;
            })
            .ReturnsAsync(new PomWorkOrderActionResult(Dto(version: 3), null, 200));
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(commandId, Row(status, version: 2), MesContext);

        result.Success.Should().BeTrue();
        capturedAction.Should().Be(expectedAction);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ExpectedVersion.Should().Be(2);
        capturedRequest.ClientChannel.Should().Be("MES");
        capturedRequest.GoodQty.Should().BeNull();
        capturedRequest.DefectQty.Should().BeNull();
    }

    [Theory]
    [InlineData(PomWorkOrderMetaCommands.Start, "Released", false, true)]
    [InlineData(PomWorkOrderMetaCommands.Start, "Started", false, false)]
    [InlineData(PomWorkOrderMetaCommands.Release, "Created", false, true)]
    [InlineData(PomWorkOrderMetaCommands.Release, "Released", false, false)]
    [InlineData(PomWorkOrderMetaCommands.Cancel, "Created", true, true)]
    [InlineData(PomWorkOrderMetaCommands.Cancel, "Released", false, true)]
    [InlineData(PomWorkOrderMetaCommands.Cancel, "Started", false, false)]
    [InlineData(PomWorkOrderMetaCommands.Report, "Started", false, true)]
    [InlineData(PomWorkOrderMetaCommands.Hold, "Released", false, false)]
    [InlineData(PomWorkOrderMetaCommands.Complete, "Started", true, false)]
    [InlineData(PomWorkOrderMetaCommands.ReleaseHold, "Started", true, true)]
    [InlineData(PomWorkOrderMetaCommands.ReleaseHold, "Completed", true, false)]
    public void CanExecute_enforces_operator_state_policy(
        string command, string status, bool held, bool expected)
    {
        var driver = new PomWorkOrderMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = Row(status: status, version: 2, held: held);

        driver.CanExecute(command, row, MobileContext).CanExecute.Should().Be(expected);
    }

    [Theory]
    [InlineData(PomWorkOrderMetaCommands.Release, "Created")]
    [InlineData(PomWorkOrderMetaCommands.Cancel, "Released")]
    public void Route_bound_work_order_keeps_management_transitions(string command, string status)
    {
        var driver = new PomWorkOrderMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = Row(status, version: 2);
        row["ROUTING_SCOPE"] = "SerialRoute";
        row["ROUTING_ID"] = "ROUTE-01";

        driver.CanExecute(command, row, MesContext).CanExecute.Should().BeTrue();
    }

    [Theory]
    [InlineData(PomWorkOrderMetaCommands.Start)]
    [InlineData(PomWorkOrderMetaCommands.Report)]
    [InlineData(PomWorkOrderMetaCommands.Complete)]
    public void Route_bound_work_order_requires_lot_execution_flow(string command)
    {
        var driver = new PomWorkOrderMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = Row(status: command == PomWorkOrderMetaCommands.Start ? "Released" : "Started", version: 2);
        row["ROUTING_ID"] = "ROUTE-01";
        row["ROUTING_STEP_NO"] = 20;

        var availability = driver.CanExecute(command, row, MobileContext);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain("LOT").And.Contain("Track-In/Track-Out");
    }

    [Theory]
    [InlineData(PomWorkOrderMetaCommands.Start, "Released")]
    [InlineData(PomWorkOrderMetaCommands.Report, "Started")]
    [InlineData(PomWorkOrderMetaCommands.Complete, "Started")]
    public void Serial_route_work_order_requires_full_lot_routing_flow(string command, string status)
    {
        var driver = new PomWorkOrderMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = Row(status: status, version: 2);
        row["ROUTING_SCOPE"] = "SerialRoute";

        var availability = driver.CanExecute(command, row, MobileContext);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain("전체 라우팅")
            .And.Contain("첫 공정").And.Contain("마지막 공정");
    }

    [Theory]
    [InlineData(PomWorkOrderMetaCommands.Hold, false)]
    [InlineData(PomWorkOrderMetaCommands.ReleaseHold, true)]
    public void Route_bound_work_order_keeps_safety_hold_actions(string command, bool held)
    {
        var driver = new PomWorkOrderMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = Row(status: "Started", version: 2, held: held);
        row["ROUTING_ID"] = "ROUTE-01";

        driver.CanExecute(command, row, MobileContext).CanExecute.Should().BeTrue();
    }

    [Fact]
    public async Task Complete_rejects_zero_or_over_started_quantity_before_http_call()
    {
        var api = new Mock<IApiClient>();
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);
        var zero = Row(status: "Started", version: 2, complete: 0m, scrap: 0m);
        var over = Row(status: "Started", version: 2, complete: 9m, scrap: 2m);
        over["START_QTY"] = 10m;

        (await driver.ExecuteAsync(PomWorkOrderMetaCommands.Complete, zero, MobileContext)).StatusCode.Should().Be(400);
        (await driver.ExecuteAsync(PomWorkOrderMetaCommands.Complete, over, MobileContext)).StatusCode.Should().Be(400);
        api.Verify(a => a.ExecutePomWorkOrderActionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PomWorkOrderActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Catalog_exposes_bridge_actions_and_rejects_duplicate_ids()
    {
        var api = new Mock<IApiClient>();
        var driver = new PomWorkOrderMetaCommandDriver(api.Object);
        var catalog = new MetaCommandDriverCatalog([driver]);

        catalog.Commands.Should().Contain(c =>
            c.Id == PomWorkOrderMetaCommands.Start
            && c.RequiredPermission == "pom:execute"
            && c.ExecutionMode == MetaCommandExecutionMode.PerRow
            && c.Effect == MetaCommandEffect.Mutating);
        catalog.Commands.Should().Contain(c =>
            c.Id == PomWorkOrderMetaCommands.Create
            && c.RequiredPermission == "pom:manage"
            && c.ExecutionMode == MetaCommandExecutionMode.PerRow
            && c.Effect == MetaCommandEffect.Mutating);
        catalog.Commands.Should().Contain(c =>
            c.Id == PomWorkOrderMetaCommands.Release
            && c.RequiredPermission == "pom:manage");
        catalog.Commands.Should().Contain(c =>
            c.Id == PomWorkOrderMetaCommands.Cancel
            && c.RequiredPermission == "pom:manage");
        catalog.TryGetDescriptor(PomWorkOrderMetaCommands.Start, out var descriptor).Should().BeTrue();
        descriptor.Should().NotBeNull();
        descriptor!.ExecutionMode.Should().Be(MetaCommandExecutionMode.PerRow);
        descriptor.Effect.Should().Be(MetaCommandEffect.Mutating);
        var act = () => new MetaCommandDriverCatalog([driver, driver]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*registered more than once*");
    }

    [Fact]
    public void Legacy_driver_contract_defaults_to_per_row_mutating()
    {
        var catalog = new MetaCommandDriverCatalog([new LegacyMetaCommandDriver()]);

        catalog.Commands.Should().ContainSingle(command =>
            command.Id == LegacyMetaCommandDriver.CommandId
            && command.RequiredPermission == "legacy:execute"
            && command.ExecutionMode == MetaCommandExecutionMode.PerRow
            && command.Effect == MetaCommandEffect.Mutating);
    }

    [Fact]
    public void MetaScreen_routes_button_through_catalog_with_pop_device_context()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var definition = new ScreenDefinition(
            "POP_EXEC", "POP 실행", Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children = [new ButtonWidget { Label = "시작", Command = PomWorkOrderMetaCommands.Start }]
            });
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(p => p.GetAsync("POP_EXEC", It.IsAny<CancellationToken>())).ReturnsAsync(definition);
        provider.Setup(p => p.Get("POP_EXEC")).Returns(definition);

        MetaCommandExecutionContext? captured = null;
        var catalog = new Mock<IMetaCommandDriverCatalog>();
        catalog.Setup(c => c.Contains(PomWorkOrderMetaCommands.Start)).Returns(true);
        catalog.Setup(c => c.CanExecute(
                PomWorkOrderMetaCommands.Start,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>()))
            .Returns(MetaCommandAvailability.Enabled);
        catalog.Setup(c => c.ExecuteAsync(
                PomWorkOrderMetaCommands.Start,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, object?>, MetaCommandExecutionContext, CancellationToken>(
                (_, _, executionContext, _) => captured = executionContext)
            .ReturnsAsync(MetaCommandResult.Succeeded());

        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(new Mock<IApiClient>().Object);
        ctx.Services.AddSingleton(catalog.Object);
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("deviceId", "KIOSK-03"));
        var cut = ctx.Render<MetaScreen>(parameters => parameters
            .Add(component => component.UiId, "POP_EXEC")
            .Add(component => component.ClientChannel, "POP"));

        cut.Find("button.layout-command").Click();

        cut.WaitForAssertion(() =>
        {
            captured.Should().NotBeNull();
            captured!.UiId.Should().Be("POP_EXEC");
            captured.ClientChannel.Should().Be("POP");
            captured.DeviceId.Should().Be("KIOSK-03");
        });
    }

    [Fact]
    public void MetaScreen_keeps_409_reason_and_does_not_reload_after_failed_bridge_command()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        var definition = new ScreenDefinition(
            "MES_EXEC", "MES 실행", Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget
                    {
                        QueryId = "POM.WorkOrderList",
                        Columns = [new GridColumnDefinition("WORK_ORDER_ID", "작업지시")]
                    },
                    new ButtonWidget { Label = "시작", Command = PomWorkOrderMetaCommands.Start }
                ]
            });
        var provider = new Mock<IScreenDefinitionProvider>();
        provider.Setup(p => p.GetAsync("MES_EXEC", It.IsAny<CancellationToken>())).ReturnsAsync(definition);
        provider.Setup(p => p.Get("MES_EXEC")).Returns(definition);
        var api = new Mock<IApiClient>();
        api.Setup(a => a.ExecuteQueryAsync(
                "POM.WorkOrderList", It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Dictionary<string, object?>>());
        var catalog = new Mock<IMetaCommandDriverCatalog>();
        catalog.Setup(c => c.Contains(PomWorkOrderMetaCommands.Start)).Returns(true);
        catalog.Setup(c => c.CanExecute(
                PomWorkOrderMetaCommands.Start,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>()))
            .Returns(MetaCommandAvailability.Enabled);
        catalog.Setup(c => c.ExecuteAsync(
                PomWorkOrderMetaCommands.Start,
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<MetaCommandExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MetaCommandResult.Failed("Current version: 9.", 409));
        ctx.Services.AddSingleton(provider.Object);
        ctx.Services.AddSingleton(api.Object);
        ctx.Services.AddSingleton(catalog.Object);
        var cut = ctx.Render<MetaScreen>(p => p.Add(c => c.UiId, "MES_EXEC"));
        cut.WaitForAssertion(() => api.Verify(a => a.ExecuteQueryAsync(
            "POM.WorkOrderList", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once));

        cut.Find("button.layout-command").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("409").And.Contain("Current version: 9."));
        api.Verify(a => a.ExecuteQueryAsync(
            "POM.WorkOrderList", It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Once,
            "실패한 bridge 명령은 성공처럼 목록을 재조회하면 안 된다");
    }

    private static Dictionary<string, object?> Row(
        string status,
        int version,
        bool held = false,
        decimal complete = 0m,
        decimal scrap = 0m)
        => new(StringComparer.Ordinal)
        {
            ["WORK_ORDER_ID"] = "WO-100",
            ["STATUS"] = status,
            ["IS_HOLD"] = held,
            ["VERSION_NO"] = version,
            ["PLAN_QTY"] = 10m,
            ["START_QTY"] = 10m,
            ["COMPLETE_QTY"] = complete,
            ["SCRAP_QTY"] = scrap
        };

    private static Dictionary<string, object?> CreateForm(string routingScope)
        => new(StringComparer.Ordinal)
        {
            ["workOrderId"] = "WO-SERIAL-100",
            ["productionOrderId"] = "PO-100",
            ["plantId"] = "P1",
            ["workOrderName"] = "완제품 전체 라우팅",
            ["productId"] = "ITEM-1",
            ["planQty"] = 100m,
            ["routingScope"] = routingScope,
        };

    private static PomWorkOrderDto Dto(int version)
        => new(
            "WO-100", "PO-100", "P1", "작업 100", "ITEM-1",
            10m, 10m, 0m, 0m, "Started", false,
            "PROC-1", "EQ-1", "worker", null, null, DateTime.UtcNow, null,
            null, null, "WC-1", null, null, null, null, version);

    /// <summary>Commands descriptor를 오버라이드하지 않은 기존 드라이버 호환성을 검증합니다.</summary>
    private sealed class LegacyMetaCommandDriver : IMetaCommandDriver
    {
        public const string CommandId = "bridge:legacy.execute";

        public IReadOnlyCollection<string> CommandIds { get; } = [CommandId];

        public string? GetRequiredPermission(string commandId)
            => commandId == CommandId ? "legacy:execute" : null;

        public MetaCommandAvailability CanExecute(
            string commandId,
            IReadOnlyDictionary<string, object?> parameters,
            MetaCommandExecutionContext context)
            => MetaCommandAvailability.Enabled;

        public Task<MetaCommandResult> ExecuteAsync(
            string commandId,
            IReadOnlyDictionary<string, object?> parameters,
            MetaCommandExecutionContext context,
            CancellationToken ct = default)
            => Task.FromResult(MetaCommandResult.Succeeded());
    }
}
