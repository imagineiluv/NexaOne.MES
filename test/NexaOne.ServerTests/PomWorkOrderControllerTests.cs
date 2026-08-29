using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// Work-order execution HTTP boundary regression tests.
/// These tests keep <c>POM_WORK_ORDER</c> state changes behind <see cref="IPomWorkOrderBridge"/>
/// and verify that the authenticated actor and client execution context are never lost.
/// </summary>
public sealed class PomWorkOrderControllerTests
{
    private const string WorkOrderId = "WO-20260714-01";
    private const string UserId = "operator-7";
    private const int ExpectedVersion = 17;
    private const string Channel = "POP";
    private const string DeviceId = "KIOSK-01";

    [Fact]
    public async Task Create_forwards_serial_route_scope_to_bridge()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = StrictBridge();
        bridge.Setup(x => x.CreateAsync(
                WorkOrderId, "PO-20260714-01", "PLANT-01", "제품 전체 라우팅 작업",
                "PRODUCT-01", 100m, null, null, null, null, UserId, UserId,
                "ROUTING-01", null, "WC-01", "AREA-01", "Production", null,
                "한 작업지시로 전체 공정을 순차 실행", "SerialRoute", cancellation.Token))
            .ReturnsAsync(Result.Success(WorkOrder()));

        var request = new CreatePomWorkOrderRequest(
            WorkOrderId,
            "PO-20260714-01",
            "PLANT-01",
            "제품 전체 라우팅 작업",
            "PRODUCT-01",
            100m,
            null,
            null,
            null,
            null,
            UserId,
            "ROUTING-01",
            null,
            "WC-01",
            "AREA-01",
            "Production",
            null,
            "한 작업지시로 전체 공정을 순차 실행",
            "SerialRoute");

        var result = await Controller(bridge).Create(request, cancellation.Token);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Start_forwards_execution_context_to_bridge()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = StrictBridge();
        bridge.Setup(x => x.StartAsync(
                WorkOrderId, ExpectedVersion, UserId, Channel, "idem-start", DeviceId,
                "shift start", cancellation.Token))
            .ReturnsAsync(Result.Success(WorkOrder()));

        var result = await Controller(bridge).Start(
            WorkOrderId,
            new PomWorkOrderOperationRequest("idem-start", ExpectedVersion, Channel, DeviceId, "shift start"),
            cancellation.Token);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Report_forwards_quantities_and_execution_context_to_bridge()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = StrictBridge();
        bridge.Setup(x => x.ReportAsync(
                WorkOrderId, 12.5m, 0.5m, ExpectedVersion, UserId, Channel, "idem-report",
                DeviceId, "hourly report", cancellation.Token))
            .ReturnsAsync(Result.Success(WorkOrder()));

        var result = await Controller(bridge).Report(
            WorkOrderId,
            new ReportPomWorkOrderRequest(
                12.5m, 0.5m, "idem-report", ExpectedVersion, Channel, DeviceId, "hourly report"),
            cancellation.Token);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Hold_forwards_execution_context_to_bridge()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = StrictBridge();
        bridge.Setup(x => x.HoldAsync(
                WorkOrderId, ExpectedVersion, UserId, Channel, "idem-hold", DeviceId,
                "material shortage", cancellation.Token))
            .ReturnsAsync(Result.Success(WorkOrder()));

        var result = await Controller(bridge).Hold(
            WorkOrderId,
            new PomWorkOrderOperationRequest(
                "idem-hold", ExpectedVersion, Channel, DeviceId, "material shortage"),
            cancellation.Token);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task ReleaseHold_forwards_execution_context_to_bridge()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = StrictBridge();
        bridge.Setup(x => x.ReleaseHoldAsync(
                WorkOrderId, ExpectedVersion, UserId, Channel, "idem-release-hold", DeviceId,
                "material supplied", cancellation.Token))
            .ReturnsAsync(Result.Success(WorkOrder()));

        var result = await Controller(bridge).ReleaseHold(
            WorkOrderId,
            new PomWorkOrderOperationRequest(
                "idem-release-hold", ExpectedVersion, Channel, DeviceId, "material supplied"),
            cancellation.Token);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Fact]
    public async Task Complete_forwards_final_quantities_and_execution_context_to_bridge()
    {
        using var cancellation = new CancellationTokenSource();
        var bridge = StrictBridge();
        bridge.Setup(x => x.CompleteAsync(
                WorkOrderId, 99m, 1m, ExpectedVersion, UserId, Channel, "idem-complete",
                DeviceId, "final confirmation", cancellation.Token))
            .ReturnsAsync(Result.Success(WorkOrder()));

        var result = await Controller(bridge).Complete(
            WorkOrderId,
            new ReportPomWorkOrderRequest(
                99m, 1m, "idem-complete", ExpectedVersion, Channel, DeviceId, "final confirmation"),
            cancellation.Token);

        result.Should().BeOfType<OkObjectResult>();
        bridge.VerifyAll();
    }

    [Theory]
    [InlineData(nameof(PomWorkOrderController.Create))]
    [InlineData(nameof(PomWorkOrderController.Release))]
    [InlineData(nameof(PomWorkOrderController.Start))]
    [InlineData(nameof(PomWorkOrderController.Report))]
    [InlineData(nameof(PomWorkOrderController.Hold))]
    [InlineData(nameof(PomWorkOrderController.ReleaseHold))]
    [InlineData(nameof(PomWorkOrderController.Complete))]
    [InlineData(nameof(PomWorkOrderController.Cancel))]
    public async Task Mutation_rejects_missing_actor_claim_before_calling_bridge(string actionName)
    {
        var bridge = StrictBridge();
        var controller = Controller(bridge, actor: null);
        var operation = new PomWorkOrderOperationRequest(
            "idem-no-actor", ExpectedVersion, Channel, DeviceId, "audit-required");
        var report = new ReportPomWorkOrderRequest(
            9m, 1m, "idem-no-actor", ExpectedVersion, Channel, DeviceId, "audit-required");

        Task<IActionResult> invocation = actionName switch
        {
            nameof(PomWorkOrderController.Create) => controller.Create(
                new CreatePomWorkOrderRequest(
                    WorkOrderId,
                    "PO-20260714-01",
                    "PLANT-01",
                    "Assembly work order",
                    "PRODUCT-01",
                    100m,
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddDays(1),
                    "PROCESS-10",
                    "EQ-01",
                    UserId),
                CancellationToken.None),
            nameof(PomWorkOrderController.Release) => controller.Release(
                WorkOrderId, operation, CancellationToken.None),
            nameof(PomWorkOrderController.Start) => controller.Start(
                WorkOrderId, operation, CancellationToken.None),
            nameof(PomWorkOrderController.Report) => controller.Report(
                WorkOrderId, report, CancellationToken.None),
            nameof(PomWorkOrderController.Hold) => controller.Hold(
                WorkOrderId, operation, CancellationToken.None),
            nameof(PomWorkOrderController.ReleaseHold) => controller.ReleaseHold(
                WorkOrderId, operation, CancellationToken.None),
            nameof(PomWorkOrderController.Complete) => controller.Complete(
                WorkOrderId, report, CancellationToken.None),
            nameof(PomWorkOrderController.Cancel) => controller.Cancel(
                WorkOrderId, operation, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(actionName), actionName, null),
        };

        var result = await invocation;

        result.Should().BeOfType<UnauthorizedResult>();
        bridge.Invocations.Should().BeEmpty("an unauditable work-order mutation must fail closed");
    }

    [Theory]
    [InlineData(nameof(PomWorkOrderController.Start))]
    [InlineData(nameof(PomWorkOrderController.Report))]
    [InlineData(nameof(PomWorkOrderController.Hold))]
    [InlineData(nameof(PomWorkOrderController.ReleaseHold))]
    [InlineData(nameof(PomWorkOrderController.Complete))]
    public void Execution_action_requires_pom_execute_permission(string actionName)
    {
        var method = typeof(PomWorkOrderController).GetMethod(
            actionName,
            BindingFlags.Instance | BindingFlags.Public);

        method.Should().NotBeNull();
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        permission.Should().NotBeNull();
        permission!.Policy.Should().Be(
            RequirePermissionAttribute.PolicyPrefix + Permissions.PomExecute);
    }

    [Theory]
    [InlineData(nameof(PomWorkOrderController.Start))]
    [InlineData(nameof(PomWorkOrderController.Report))]
    [InlineData(nameof(PomWorkOrderController.Hold))]
    [InlineData(nameof(PomWorkOrderController.ReleaseHold))]
    [InlineData(nameof(PomWorkOrderController.Complete))]
    public async Task Execution_action_maps_bridge_conflict_to_409(string actionName)
    {
        var bridge = StrictBridge();
        var failure = Result.Failure<PomWorkOrderDto>(
            Error.Conflict("POM_WORK_ORDER version conflict."));
        var operation = new PomWorkOrderOperationRequest(
            "idem-conflict", ExpectedVersion, Channel, DeviceId, "retry");
        var report = new ReportPomWorkOrderRequest(
            9m, 1m, "idem-conflict", ExpectedVersion, Channel, DeviceId, "retry");

        IActionResult result;
        switch (actionName)
        {
            case nameof(PomWorkOrderController.Start):
                bridge.Setup(x => x.StartAsync(
                        WorkOrderId, ExpectedVersion, UserId, Channel, operation.IdempotencyKey,
                        DeviceId, operation.Remark, CancellationToken.None))
                    .ReturnsAsync(failure);
                result = await Controller(bridge).Start(
                    WorkOrderId, operation, CancellationToken.None);
                break;
            case nameof(PomWorkOrderController.Report):
                bridge.Setup(x => x.ReportAsync(
                        WorkOrderId, report.GoodQty, report.DefectQty, ExpectedVersion, UserId,
                        Channel, report.IdempotencyKey, DeviceId, report.Remark, CancellationToken.None))
                    .ReturnsAsync(failure);
                result = await Controller(bridge).Report(
                    WorkOrderId, report, CancellationToken.None);
                break;
            case nameof(PomWorkOrderController.Hold):
                bridge.Setup(x => x.HoldAsync(
                        WorkOrderId, ExpectedVersion, UserId, Channel, operation.IdempotencyKey,
                        DeviceId, operation.Remark, CancellationToken.None))
                    .ReturnsAsync(failure);
                result = await Controller(bridge).Hold(
                    WorkOrderId, operation, CancellationToken.None);
                break;
            case nameof(PomWorkOrderController.ReleaseHold):
                bridge.Setup(x => x.ReleaseHoldAsync(
                        WorkOrderId, ExpectedVersion, UserId, Channel, operation.IdempotencyKey,
                        DeviceId, operation.Remark, CancellationToken.None))
                    .ReturnsAsync(failure);
                result = await Controller(bridge).ReleaseHold(
                    WorkOrderId, operation, CancellationToken.None);
                break;
            case nameof(PomWorkOrderController.Complete):
                bridge.Setup(x => x.CompleteAsync(
                        WorkOrderId, report.GoodQty, report.DefectQty, ExpectedVersion, UserId,
                        Channel, report.IdempotencyKey, DeviceId, report.Remark, CancellationToken.None))
                    .ReturnsAsync(failure);
                result = await Controller(bridge).Complete(
                    WorkOrderId, report, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(actionName), actionName, null);
        }

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.Value.Should().Be(failure.Error);
        bridge.VerifyAll();
    }

    /// <summary>Creates a controller with the same authenticated user identity consumed in production.</summary>
    private static PomWorkOrderController Controller(
        Mock<IPomWorkOrderBridge> bridge,
        string? actor = UserId)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        if (actor is not null)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actor));
        var controller = new PomWorkOrderController(bridge.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                },
            },
        };
        return controller;
    }

    private static Mock<IPomWorkOrderBridge> StrictBridge() =>
        new(MockBehavior.Strict);

    private static PomWorkOrderDto WorkOrder() => new(
        WorkOrderId,
        "PO-20260714-01",
        "PLANT-01",
        "Assembly work order",
        "PRODUCT-01",
        100m,
        100m,
        0m,
        0m,
        "Started",
        false,
        "PROCESS-10",
        "EQ-01",
        UserId,
        null,
        null,
        DateTime.UtcNow,
        null,
        "ROUTING-01",
        10,
        "WC-01",
        "AREA-01",
        "Production",
        null,
        null,
        ExpectedVersion,
        "Operation");
}
