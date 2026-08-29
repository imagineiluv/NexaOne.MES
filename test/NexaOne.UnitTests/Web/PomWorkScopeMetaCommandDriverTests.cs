using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

public sealed class PomWorkScopeMetaCommandDriverTests
{
    private static readonly MetaCommandExecutionContext MesContext =
        new("FACTORY_PPM_WORK_ORDER", "MES", null);

    private static readonly MetaCommandExecutionContext PopContext =
        new("FACTORY_PPM_WORK_ORDER", "POP", "WASH-KIOSK-01");

    [Fact]
    public async Task Create_maps_campaign_batch_and_carrier_fields_to_typed_api()
    {
        PomWorkScopeCreateRequest? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(client => client.CreatePomWorkScopeAsync(
                It.IsAny<PomWorkScopeCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PomWorkScopeCreateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PomWorkScopeActionResult(Dto("BATCH-01", "Created", 1), null, 201));
        var driver = new PomWorkScopeMetaCommandDriver(api.Object);
        var form = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["workScopeId"] = "BATCH-01",
            ["plantId"] = "P1",
            ["scopeType"] = "batch",
            ["targetId"] = "BATCH-01",
            ["name"] = "Carrier 세척 Batch",
            ["parentScopeId"] = "CAMPAIGN-2026-08",
            ["carrierId"] = "CARRIER-0007",
            ["equipmentId"] = "WASH-01",
            ["processId"] = "CLEAN",
            ["recipeId"] = "WASH-RECIPE-01",
            ["recipeVersion"] = "3",
            ["planQty"] = "12.5",
            ["ownerId"] = "operator-01",
            ["description"] = "LOT 없는 이동용기 세척",
        };

        var result = await driver.ExecuteAsync(PomWorkScopeMetaCommands.Create, form, MesContext);

        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        captured.Should().NotBeNull();
        captured!.ScopeType.Should().Be("Batch");
        captured.TargetId.Should().Be("BATCH-01");
        captured.ParentScopeId.Should().Be("CAMPAIGN-2026-08");
        captured.CarrierId.Should().Be("CARRIER-0007");
        captured.EquipmentId.Should().Be("WASH-01");
        captured.RecipeVersion.Should().Be(3);
        captured.PlanQty.Should().Be(12.5m);
        captured.WorkOrderId.Should().BeNull("이 설비 작업은 생산 W/O가 아닌 WorkScope로 등록한다");
        captured.IdempotencyKey.Should().StartWith("meta:create:");
        captured.IdempotencyKey!.Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Create_uses_explicit_idempotency_key_when_a_host_replays_a_request()
    {
        PomWorkScopeCreateRequest? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(client => client.CreatePomWorkScopeAsync(
                It.IsAny<PomWorkScopeCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PomWorkScopeCreateRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PomWorkScopeActionResult(Dto("CARRIER-01", "Created", 1), null, 201));
        var form = CreateForm("Carrier", "CARRIER-01");
        form["carrierId"] = "CARRIER-01";
        form["idempotencyKey"] = "wash-create-retry-0001";

        var result = await new PomWorkScopeMetaCommandDriver(api.Object)
            .ExecuteAsync(PomWorkScopeMetaCommands.Create, form, PopContext);

        result.Success.Should().BeTrue();
        captured!.IdempotencyKey.Should().Be("wash-create-retry-0001");
    }

    [Theory]
    [InlineData("Campaign", "CAMPAIGN-01", "Campaign은 최상위")]
    [InlineData("Carrier", "CARRIER-01", "Carrier 범위의 Carrier ID")]
    [InlineData("Equipment", "EQ-01", "설비 범위의 설비 ID")]
    public void Create_rejects_invalid_parent_or_target_identity(
        string scopeType,
        string targetId,
        string reason)
    {
        var driver = new PomWorkScopeMetaCommandDriver(new Mock<IApiClient>().Object);
        var form = CreateForm(scopeType, targetId);
        form["parentScopeId"] = "PARENT-01";
        if (scopeType == "Carrier") form["carrierId"] = "OTHER-CARRIER";
        if (scopeType == "Equipment") form["equipmentId"] = "OTHER-EQUIPMENT";

        var availability = driver.CanExecute(PomWorkScopeMetaCommands.Create, form, MesContext);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain(reason);
    }

    [Fact]
    public async Task Start_maps_version_channel_device_carrier_and_stable_idempotency_key()
    {
        var requests = new List<PomWorkScopeActionRequest>();
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecutePomWorkScopeActionAsync(
                "start", "CARRIER-01", It.IsAny<PomWorkScopeActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PomWorkScopeActionRequest, CancellationToken>(
                (_, _, request, _) => requests.Add(request))
            .ReturnsAsync(new PomWorkScopeActionResult(Dto("CARRIER-01", "Started", 4), null, 200));
        var driver = new PomWorkScopeMetaCommandDriver(api.Object);
        var row = Row("Released", 3);
        row["WORK_SCOPE_ID"] = "CARRIER-01";
        row["CARRIER_ID"] = "CARRIER-01";

        (await driver.ExecuteAsync(PomWorkScopeMetaCommands.Start, row, PopContext)).Success.Should().BeTrue();
        (await driver.ExecuteAsync(PomWorkScopeMetaCommands.Start, row, PopContext)).Success.Should().BeTrue();

        requests.Should().HaveCount(2);
        requests[0].ExpectedVersion.Should().Be(3);
        requests[0].ClientChannel.Should().Be("POP");
        requests[0].DeviceId.Should().Be("WASH-KIOSK-01");
        requests[0].CarrierId.Should().Be("CARRIER-01");
        requests[0].IdempotencyKey.Should().Be(requests[1].IdempotencyKey,
            "같은 버전의 응답 유실 재시도는 서버 멱등 원장을 재생해야 한다");
        requests[0].IdempotencyKey.Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Report_sends_absolute_quality_totals_and_result_context()
    {
        PomWorkScopeActionRequest? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecutePomWorkScopeActionAsync(
                "report", "BATCH-01", It.IsAny<PomWorkScopeActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PomWorkScopeActionRequest, CancellationToken>(
                (_, _, request, _) => captured = request)
            .ReturnsAsync(new PomWorkScopeActionResult(Dto("BATCH-01", "Started", 6), null, 200));
        var row = Row("Started", 5, complete: 10m, scrap: 2m);
        row["PLAN_QTY"] = 20m;
        row["START_QTY"] = 20m;
        row["RESULT_CODE"] = "PASS_WITH_SCRAP";
        row["RESULT_METADATA_JSON"] = "{\"cleaningProgram\":\"RINSE-02\"}";

        var result = await new PomWorkScopeMetaCommandDriver(api.Object)
            .ExecuteAsync(PomWorkScopeMetaCommands.Report, row, MesContext);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.GoodQty.Should().Be(10m, "COMPLETE_QTY는 양품 현재 누계로 전달한다");
        captured.DefectQty.Should().Be(2m, "SCRAP_QTY는 이상 현재 누계로 전달한다");
        captured.ResultCode.Should().Be("PASS_WITH_SCRAP");
        captured.ResultMetadataJson.Should().Contain("RINSE-02");
    }

    [Fact]
    public async Task Complete_rejects_zero_or_over_plan_before_http_call()
    {
        var api = new Mock<IApiClient>();
        var driver = new PomWorkScopeMetaCommandDriver(api.Object);
        var zero = Row("Started", 2, complete: 0m, scrap: 0m);
        var over = Row("Started", 2, complete: 9m, scrap: 2m);
        over["START_QTY"] = 10m;

        (await driver.ExecuteAsync(PomWorkScopeMetaCommands.Complete, zero, MesContext))
            .StatusCode.Should().Be(400);
        (await driver.ExecuteAsync(PomWorkScopeMetaCommands.Complete, over, MesContext))
            .StatusCode.Should().Be(400);
        api.Verify(client => client.ExecutePomWorkScopeActionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PomWorkScopeActionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PomWorkScopeMetaCommands.Release, "Created", false, true)]
    [InlineData(PomWorkScopeMetaCommands.Release, "Released", false, false)]
    [InlineData(PomWorkScopeMetaCommands.Start, "Released", false, true)]
    [InlineData(PomWorkScopeMetaCommands.Start, "Started", false, false)]
    [InlineData(PomWorkScopeMetaCommands.Hold, "Started", false, true)]
    [InlineData(PomWorkScopeMetaCommands.Hold, "Started", true, false)]
    [InlineData(PomWorkScopeMetaCommands.ReleaseHold, "Started", true, true)]
    [InlineData(PomWorkScopeMetaCommands.ReleaseHold, "Completed", true, false)]
    [InlineData(PomWorkScopeMetaCommands.Cancel, "Started", false, true)]
    [InlineData(PomWorkScopeMetaCommands.Cancel, "Completed", false, false)]
    public void CanExecute_enforces_work_scope_lifecycle_policy(
        string command,
        string status,
        bool held,
        bool expected)
    {
        var driver = new PomWorkScopeMetaCommandDriver(new Mock<IApiClient>().Object);
        var row = Row(status, version: 2, held: held);
        if (command is PomWorkScopeMetaCommands.Report or PomWorkScopeMetaCommands.Complete)
        {
            row["COMPLETE_QTY"] = 1m;
            row["SCRAP_QTY"] = 0m;
        }

        driver.CanExecute(command, row, MesContext).CanExecute.Should().Be(expected);
    }

    [Fact]
    public async Task Conflict_status_and_server_reason_are_preserved()
    {
        var api = new Mock<IApiClient>();
        api.Setup(client => client.ExecutePomWorkScopeActionAsync(
                "start", "BATCH-01", It.IsAny<PomWorkScopeActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PomWorkScopeActionResult(null, "Current version: 9.", 409));

        var result = await new PomWorkScopeMetaCommandDriver(api.Object)
            .ExecuteAsync(PomWorkScopeMetaCommands.Start, Row("Released", 3), MesContext);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Error.Should().Be("Current version: 9.");
    }

    [Fact]
    public void Catalog_exposes_all_work_scope_actions_with_safe_permissions()
    {
        var driver = new PomWorkScopeMetaCommandDriver(new Mock<IApiClient>().Object);
        var catalog = new MetaCommandDriverCatalog([driver]);

        catalog.Commands.Should().HaveCount(PomWorkScopeMetaCommands.All.Count);
        catalog.Commands.Should().Contain(command =>
            command.Id == PomWorkScopeMetaCommands.Create
            && command.RequiredPermission == "pom:manage"
            && command.ExecutionMode == MetaCommandExecutionMode.PerRow
            && command.Effect == MetaCommandEffect.Mutating);
        catalog.Commands.Should().Contain(command =>
            command.Id == PomWorkScopeMetaCommands.Report
            && command.RequiredPermission == "pom:execute");
        catalog.Commands.Should().Contain(command =>
            command.Id == PomWorkScopeMetaCommands.Cancel
            && command.RequiredPermission == "pom:manage");
    }

    private static Dictionary<string, object?> Row(
        string status,
        int version,
        bool held = false,
        decimal complete = 0m,
        decimal scrap = 0m)
        => new(StringComparer.Ordinal)
        {
            ["WORK_SCOPE_ID"] = "BATCH-01",
            ["STATUS"] = status,
            ["IS_HOLD"] = held,
            ["VERSION_NO"] = version,
            ["PLAN_QTY"] = 10m,
            ["START_QTY"] = 10m,
            ["COMPLETE_QTY"] = complete,
            ["SCRAP_QTY"] = scrap,
        };

    private static Dictionary<string, object?> CreateForm(string scopeType, string targetId)
        => new(StringComparer.Ordinal)
        {
            ["workScopeId"] = targetId,
            ["plantId"] = "P1",
            ["scopeType"] = scopeType,
            ["targetId"] = targetId,
            ["name"] = $"{scopeType} 작업",
        };

    private static PomWorkScopeDto Dto(string id, string status, int version)
        => new(
            WorkScopeId: id,
            PlantId: "P1",
            ScopeType: id.StartsWith("CARRIER", StringComparison.Ordinal) ? "Carrier" : "Batch",
            TargetId: id,
            Name: "작업 범위",
            ParentScopeId: null,
            EquipmentId: "WASH-01",
            ProductId: null,
            ProcessId: "CLEAN",
            RecipeId: "WASH-RECIPE-01",
            RecipeVersion: 1,
            PlanQty: 10m,
            StartQty: 10m,
            CompleteQty: 0m,
            ScrapQty: 0m,
            OwnerId: "operator-01",
            Status: status,
            IsHold: false,
            StartedAt: DateTime.UtcNow,
            CompletedAt: null,
            Description: null,
            VersionNo: version,
            CreatedAt: DateTime.UtcNow,
            CreatedBy: "operator-01",
            UpdatedAt: DateTime.UtcNow,
            UpdatedBy: "operator-01",
            WorkOrderId: null,
            CarrierId: id.StartsWith("CARRIER", StringComparison.Ordinal) ? id : null);
}
