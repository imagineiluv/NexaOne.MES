using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ems;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class MaintenanceExecutionControllerTests
{
    [Fact]
    public async Task Mutations_fail_closed_without_an_authenticated_actor()
    {
        var bridge = new FakeBridge();
        var controller = Controller(bridge, new ClaimsPrincipal(new ClaimsIdentity("test")));
        var request = new MaintenanceCheckRequest(
            "CHECK-1", 1, "Temperature", DateTime.UtcNow,
            new MaintenanceCommandRequest("check-key"), IsPass: true);

        var result = await controller.RecordCheck("WO-1", request, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        bridge.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task Route_ids_and_claim_actor_are_the_only_trusted_execution_identity()
    {
        var bridge = new FakeBridge();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "login-maintainer")], "test"));
        var controller = Controller(bridge, principal);
        var at = DateTime.UtcNow;

        await controller.RecordCheck("ROUTE-WO", new MaintenanceCheckRequest(
            "CHECK-1", 1, "Temperature", at,
            new MaintenanceCommandRequest("check-key", "POP", "PANEL-01", "corr-1"),
            IsPass: true), CancellationToken.None);
        await controller.StartLabor("ROUTE-WO", new MaintenanceLaborStartRequest(
            "LABOR-1", "Work", at,
            new MaintenanceCommandRequest("start-key")), CancellationToken.None);
        await controller.CompleteLabor("ROUTE-LABOR", new MaintenanceLaborCompleteRequest(
            1, at.AddHours(1), new MaintenanceCommandRequest("end-key")),
            CancellationToken.None);

        bridge.Check!.WorkOrderId.Should().Be("ROUTE-WO");
        bridge.Check.Command.ActorId.Should().Be("login-maintainer");
        bridge.Check.Command.ClientChannel.Should().Be("POP");
        bridge.LaborStart!.WorkOrderId.Should().Be("ROUTE-WO");
        bridge.LaborStart.Command.ActorId.Should().Be("login-maintainer");
        bridge.LaborComplete!.LaborId.Should().Be("ROUTE-LABOR");
        bridge.LaborComplete.Command.ActorId.Should().Be("login-maintainer");
    }

    [Theory]
    [InlineData(nameof(MaintenanceExecutionController.RecordCheck))]
    [InlineData(nameof(MaintenanceExecutionController.StartLabor))]
    [InlineData(nameof(MaintenanceExecutionController.CompleteLabor))]
    public void Every_mutation_requires_ems_manage(string actionName)
    {
        var method = typeof(MaintenanceExecutionController).GetMethod(
            actionName, BindingFlags.Instance | BindingFlags.Public);

        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be(
            RequirePermissionAttribute.PolicyPrefix + Permissions.EmsManage);
    }

    private static MaintenanceExecutionController Controller(
        IMaintenanceExecutionBridge bridge,
        ClaimsPrincipal principal) => new(bridge)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        },
    };

    private sealed class FakeBridge : IMaintenanceExecutionBridge
    {
        public int InvocationCount { get; private set; }
        public MaintenanceCheckCommand? Check { get; private set; }
        public MaintenanceLaborStartCommand? LaborStart { get; private set; }
        public MaintenanceLaborCompleteCommand? LaborComplete { get; private set; }

        public Task<Result<MaintenanceCheckDto>> RecordCheckAsync(
            MaintenanceCheckCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            Check = command;
            return Task.FromResult(Result.Success(new MaintenanceCheckDto(
                command.CheckResultId, command.WorkOrderId, command.ItemId,
                command.ItemSequence, command.CheckName, command.MeasuredValue,
                command.AttributeValue, command.Unit, command.IsPass, command.Finding,
                command.Command.ActorId, command.RecordedAt, command.Command.CorrelationId)));
        }

        public Task<Result<MaintenanceLaborDto>> StartLaborAsync(
            MaintenanceLaborStartCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LaborStart = command;
            return Task.FromResult(Result.Success(new MaintenanceLaborDto(
                command.LaborId, command.WorkOrderId, command.Command.ActorId,
                command.WorkerId, command.LaborType, command.StartedAt, null, null,
                null, command.Remark, command.Command.CorrelationId, 1)));
        }

        public Task<Result<MaintenanceLaborDto>> CompleteLaborAsync(
            MaintenanceLaborCompleteCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LaborComplete = command;
            return Task.FromResult(Result.Success(new MaintenanceLaborDto(
                command.LaborId, "WO-1", command.Command.ActorId, null, "Work",
                command.EndedAt.AddHours(-1), command.EndedAt, command.Command.ActorId,
                1m, command.Remark, command.Command.CorrelationId,
                command.ExpectedVersion + 1)));
        }
    }
}
