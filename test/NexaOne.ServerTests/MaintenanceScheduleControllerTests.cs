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

public sealed class MaintenanceScheduleControllerTests
{
    [Fact]
    public async Task Mutations_fail_closed_without_authenticated_actor()
    {
        var bridge = new FakeBridge();
        var controller = Controller(bridge, new ClaimsPrincipal(new ClaimsIdentity("test")));
        var command = new MaintenanceScheduleCreateCommand(
            "SCHEDULE-1", "PLAN-1", "Calendar", 1m, "Day",
            NextDueAt: DateTime.UtcNow, ActorId: "spoofed");

        var result = await controller.Create(command, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        bridge.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task Route_and_claim_actor_override_untrusted_body_values()
    {
        var bridge = new FakeBridge();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "login-maintainer")], "test"));
        var controller = Controller(bridge, principal);
        var update = new MaintenanceScheduleUpdateCommand(
            "BODY-SCHEDULE", 3, "PLAN-1", "Condition",
            ConditionRuleId: "RULE-1", ActorId: "spoofed");
        var acknowledgement = new MaintenanceScheduleAcknowledgeCommand(
            "BODY-SCHEDULE", 4, "ack-1", DateTime.UtcNow,
            ConditionMet: true, ActorId: "spoofed");

        var updateResult = await controller.Update("ROUTE-SCHEDULE", update, CancellationToken.None);
        var acknowledgeResult = await controller.Acknowledge(
            "ROUTE-SCHEDULE", acknowledgement, CancellationToken.None);

        updateResult.Should().BeOfType<OkObjectResult>();
        acknowledgeResult.Should().BeOfType<OkObjectResult>();
        bridge.LastUpdate!.ScheduleId.Should().Be("ROUTE-SCHEDULE");
        bridge.LastUpdate.ActorId.Should().Be("login-maintainer");
        bridge.LastAcknowledgement!.ScheduleId.Should().Be("ROUTE-SCHEDULE");
        bridge.LastAcknowledgement.ActorId.Should().Be("login-maintainer");
    }

    [Theory]
    [InlineData(nameof(MaintenanceScheduleController.Create))]
    [InlineData(nameof(MaintenanceScheduleController.Update))]
    [InlineData(nameof(MaintenanceScheduleController.Acknowledge))]
    public void Every_mutation_requires_ems_manage(string actionName)
    {
        var method = typeof(MaintenanceScheduleController).GetMethod(
            actionName, BindingFlags.Instance | BindingFlags.Public);

        method.Should().NotBeNull();
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        permission.Should().NotBeNull();
        permission!.Policy.Should().Be(
            RequirePermissionAttribute.PolicyPrefix + Permissions.EmsManage);
    }

    private static MaintenanceScheduleController Controller(
        IMaintenanceScheduleBridge bridge,
        ClaimsPrincipal principal) => new(bridge)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        },
    };

    private sealed class FakeBridge : IMaintenanceScheduleBridge
    {
        public int InvocationCount { get; private set; }
        public MaintenanceScheduleCreateCommand? LastCreate { get; private set; }
        public MaintenanceScheduleUpdateCommand? LastUpdate { get; private set; }
        public MaintenanceScheduleAcknowledgeCommand? LastAcknowledgement { get; private set; }

        public Task<Result<MaintenanceScheduleDto>> CreateAsync(
            MaintenanceScheduleCreateCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LastCreate = command;
            return Task.FromResult(Result.Success(Schedule(command.ScheduleId, command.ActorId!)));
        }

        public Task<Result<MaintenanceScheduleDto>> UpdateAsync(
            MaintenanceScheduleUpdateCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LastUpdate = command;
            return Task.FromResult(Result.Success(Schedule(command.ScheduleId, command.ActorId!)));
        }

        public Task<Result<MaintenanceScheduleAcknowledgementDto>> AcknowledgeAsync(
            MaintenanceScheduleAcknowledgeCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LastAcknowledgement = command;
            return Task.FromResult(Result.Success(new MaintenanceScheduleAcknowledgementDto(
                "ACK-1", command.ScheduleId, "PLAN-1", "Condition", null, null,
                null, null, null, "RULE-1", true, command.AcknowledgedAt,
                command.ActorId!, command.IdempotencyKey, command.ClientChannel,
                command.DeviceId, command.CorrelationId, command.Remark,
                command.ExpectedVersion, command.ExpectedVersion + 1)));
        }

        private static MaintenanceScheduleDto Schedule(string scheduleId, string actor)
        {
            var now = DateTime.UtcNow;
            return new MaintenanceScheduleDto(
                scheduleId, "PLAN-1", "Condition", null, null, "Asia/Seoul",
                null, null, null, null, null, null, "RULE-1", false, true,
                1, actor, now, actor, now);
        }
    }
}
